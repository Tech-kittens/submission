using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using Json.Schema;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public sealed record McpToolDefinition(string Name, string? Description, JsonElement InputSchema, bool ReadOnly, bool Destructive)
{
    public string SchemaDigest => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(InputSchema.GetRawText())));
}
public interface IMcpClientGateway
{
    Task<IReadOnlyList<McpToolDefinition>> DiscoverAsync(string serverId, string token, CancellationToken ct);
    Task<JsonElement> ExecuteAsync(string serverId, string token, string toolName, IReadOnlyDictionary<string, object?> arguments,
        string schemaDigest, bool approved, CancellationToken ct);
    Task HealthAsync(string serverId, string token, CancellationToken ct);
}

// Scoped to one HTTP request or durable worker operation, never shared between users.
// Multi-step plans reuse the authenticated client and periodically refresh discovery.
public sealed class McpClientGateway(IMcpServerRegistry registry, IOptions<McpSettings> settings,
    IHttpClientFactory clients, ILogger<McpClientGateway>? logger = null) : IMcpClientGateway, IAsyncDisposable
{
    private readonly Dictionary<string, McpClient> connections = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1);
    public async Task<IReadOnlyList<McpToolDefinition>> DiscoverAsync(string serverId, string token, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = Timeout(ct);
        var client = await ConnectAsync(serverId, token, timeout.Token);
        var server = registry.Get(serverId);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        if (logger?.IsEnabled(LogLevel.Debug) == true)
            logger.LogDebug("McpTiming {ServerId} {Stage} {ElapsedMs:F2}", serverId,
                "tool_discovery", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (tools.Count > 200) throw new AppException(502, "mcp_too_many_tools");
        return tools.Select(x => new McpToolDefinition(x.Name, x.Description,
            JsonSerializer.SerializeToElement(x.ProtocolTool.InputSchema),
            server.ReadOnlyTools.Contains(x.Name, StringComparer.Ordinal) && x.ProtocolTool.Annotations?.ReadOnlyHint == true,
            x.ProtocolTool.Annotations?.ReadOnlyHint != true && x.ProtocolTool.Annotations?.DestructiveHint != false)).ToArray();
    }
    public async Task<JsonElement> ExecuteAsync(string serverId, string token, string toolName,
        IReadOnlyDictionary<string, object?> arguments, string schemaDigest, bool approved, CancellationToken ct)
    {
        using var timeout = Timeout(ct);
        // Re-discovery is deliberate: an approval cannot authorize a subsequently changed tool schema.
        var tool = (await DiscoverAsync(serverId, token, timeout.Token)).SingleOrDefault(x => x.Name == toolName)
            ?? throw new AppException(409, "mcp_tool_no_longer_available");
        if (tool.SchemaDigest != schemaDigest) throw new AppException(409, "mcp_tool_schema_changed");
        if (!tool.ReadOnly && !approved) throw new AppException(409, "approval_required");
        ValidateArguments(tool, JsonSerializer.SerializeToElement(arguments));
        var client = await ConnectAsync(serverId, token, timeout.Token);
        // Never automatically retry calls: a network failure may follow a successful external write.
        var started = Stopwatch.GetTimestamp();
        var result = await client.CallToolAsync(toolName, arguments, cancellationToken: timeout.Token);
        if (logger?.IsEnabled(LogLevel.Debug) == true)
            logger.LogDebug("McpTiming {ServerId} {Stage} {ElapsedMs:F2}", serverId,
                "tool_execution", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (result.IsError == true) throw new AppException(502, "mcp_tool_reported_error");
        var json = JsonSerializer.SerializeToElement(result);
        if (json.GetRawText().Length > 131072) throw new AppException(502, "mcp_result_too_large");
        return json;
    }
    public async Task HealthAsync(string serverId, string token, CancellationToken ct)
    {
        using var timeout = Timeout(ct);
        await (await ConnectAsync(serverId, token, timeout.Token)).PingAsync(cancellationToken: timeout.Token);
    }
    public static void ValidateArguments(McpToolDefinition tool, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || arguments.GetRawText().Length > 32768)
            throw new AppException(400, "mcp_arguments_invalid");
        var schemaJson = tool.InputSchema.GetRawText();
        if (schemaJson.Length > 65536) throw new AppException(502, "mcp_schema_too_large");
        RejectRemoteReferences(JsonNode.Parse(schemaJson)!);
        if (!JsonSchema.FromText(schemaJson).Evaluate(arguments).IsValid)
            throw new AppException(400, "mcp_arguments_schema_invalid");
    }
    private async Task<McpClient> ConnectAsync(string serverId, string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new AppException(409, "mcp_reauthorization_required");
        var server = registry.Get(serverId);
        var key = serverId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        await gate.WaitAsync(ct);
        try
        {
            if (connections.TryGetValue(key, out var existing)) return existing;
            if (connections.Count >= 12) throw new AppException(429, "mcp_connection_limit");
            var transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = server.Endpoint,
                TransportMode = HttpTransportMode.AutoDetect,
                ConnectionTimeout = TimeSpan.FromSeconds(settings.Value.ConnectionTimeoutSeconds),
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
                EnableStandaloneGetStream = false
            }, clients.CreateClient("Mcp"), ownsHttpClient: true);
            try
            {
                var started = Stopwatch.GetTimestamp();
                var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
                if (logger?.IsEnabled(LogLevel.Debug) == true)
                    logger.LogDebug("McpTiming {ServerId} {Stage} {ElapsedMs:F2}", serverId,
                        "connection_setup", Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                connections.Add(key, client);
                return client;
            }
            catch { await transport.DisposeAsync(); throw; }
        }
        finally { gate.Release(); }
    }
    private CancellationTokenSource Timeout(CancellationToken ct)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.Value.ToolTimeoutSeconds));
        return timeout;
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var client in connections.Values) await client.DisposeAsync();
        gate.Dispose();
    }
    private static void RejectRemoteReferences(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var keyword in new[] { "$ref", "$dynamicRef", "$recursiveRef" })
                if (obj[keyword] is JsonValue value && value.TryGetValue<string>(out var uri) && !uri.StartsWith('#'))
                    throw new AppException(400, "mcp_schema_remote_reference_rejected");
            foreach (var child in obj.Select(x => x.Value).Where(x => x is not null)) RejectRemoteReferences(child!);
        }
        else if (node is JsonArray array)
            foreach (var child in array.Where(x => x is not null)) RejectRemoteReferences(child!);
    }
}
