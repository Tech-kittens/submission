using System.Text.Json;
using Microsoft.Extensions.Logging;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public sealed class McpSkillRegistry(IMcpConnectionService connections, ILogger<McpSkillRegistry> logger) : IMcpSkillRegistry
{
    private static readonly JsonElement EmptySchema = JsonSerializer.SerializeToElement(new
    { type = "object", properties = new { }, additionalProperties = false });
    private static readonly JsonElement ResultSchema = JsonSerializer.SerializeToElement(new { type = "object" });
    private static readonly JsonElement WorkflowSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object", properties = new { steps = new { type = "array", minItems = 1, maxItems = 8,
            items = new { type = "object", properties = new { skillId = new { type = "string" }, arguments = new { type = "object" } },
                required = new[] { "skillId", "arguments" }, additionalProperties = false } } },
        required = new[] { "steps" }, additionalProperties = false
    });

    public Task<McpSkillDefinition?> GetSkillAsync(string skillId, CancellationToken ct) =>
        Task.FromResult(McpSkillCatalog.All.TryGetValue(skillId, out var item) ? Definition(item) : null);

    public async Task<IReadOnlyList<McpSkillDefinition>> GetAvailableSkillsAsync(string userId, CancellationToken ct)
    {
        var byId = McpSkillCatalog.All.Values.ToDictionary(x => x.Id, Definition, StringComparer.Ordinal);
        foreach (var connection in await connections.ListAsync(userId, ct))
        {
            if (connection.Status != "Connected") continue;
            foreach (var candidate in await GetSkillsForConnectionAsync(userId, connection.Id, ct))
                if (!byId.TryGetValue(candidate.Id, out var prior) || candidate.Available || !prior.Available && prior.UnavailableReason == "No connected Google account.")
                    byId[candidate.Id] = candidate;
        }
        return byId.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    public async Task<IReadOnlyList<McpSkillDefinition>> GetSkillsForConnectionAsync(string userId, string connectionId, CancellationToken ct)
    {
        var connection = (await connections.ListAsync(userId, ct)).SingleOrDefault(c => c.Id == connectionId);
        Guard.Found(connection);
        IReadOnlyList<McpToolInfo> discovered;
        try { discovered = connection!.Status == "Connected" ? await connections.ListToolsAsync(userId, connectionId, ct) : []; }
        catch (Exception e) when (e is AppException or HttpRequestException or TimeoutException)
        {
            logger.LogWarning("MCP skill discovery failed {ConnectionId} {ErrorType}", connectionId, e.GetType().Name);
            discovered = [];
        }
        var tools = discovered.Where(t => connection!.EnabledServices?.Contains(t.ServerId) == true
                && connection.EnabledTools.Contains(McpConnectionService.ToolKey(t.ServerId, t.Name), StringComparer.Ordinal))
            .ToDictionary(t => t.ServerId + "/" + t.Name, StringComparer.Ordinal);
        if (connection!.Provider != "google")
            return tools.Values.Where(t => t.ServerId.StartsWith(connection.Provider + ":", StringComparison.Ordinal))
                .Select(t => new McpSkillDefinition
                {
                    Id = "mcp." + t.ServerId + "/" + t.Name, Name = t.Name,
                    Description = t.Description ?? "Execute the connected MCP tool " + t.Name,
                    Provider = connection.Provider, Service = t.ServerId.Split(':', 2)[1],
                    ServerId = t.ServerId, ToolName = t.Name, ConnectionId = connectionId,
                    RequiredCapabilities = [t.Name], InputSchema = t.InputSchema, OutputSchema = ResultSchema,
                    PermissionLevel = t.Destructive ? SkillPermissionLevel.Destructive : t.ReadOnly ? SkillPermissionLevel.Read : SkillPermissionLevel.Write,
                    RequiresApproval = t.Destructive || !t.ReadOnly, Available = true
                }).OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
        var result = new Dictionary<string, McpSkillDefinition>(StringComparer.Ordinal);
        foreach (var template in McpSkillCatalog.All.Values.Where(x => x.Mode != "workflow"))
        {
            var definition = Definition(template);
            definition.ConnectionId = connectionId;
            if (connection!.Status != "Connected") definition.UnavailableReason = "Connection requires reauthorization.";
            else if (template.Tool is not null && !HasScope(connection, template))
                definition.UnavailableReason = "The connected account lacks the required OAuth scope.";
            else if (template.Tool is not null && tools.TryGetValue(ToolKey(template), out var tool))
            {
                definition.Available = true;
                definition.UnavailableReason = null;
                definition.InputSchema = InputSchema(template, tool);
                definition.RequiresApproval = template.Permission == SkillPermissionLevel.Destructive;
            }
            else if (template.Tool is not null) definition.UnavailableReason =
                "Required MCP tool is unavailable or not enabled for this account: " + template.Tool;
            result.Add(template.Id, definition);
        }
        foreach (var template in McpSkillCatalog.All.Values.Where(x => x.Mode == "workflow"))
        {
            var definition = Definition(template);
            definition.ConnectionId = connectionId;
            definition.RequiredCapabilities = template.Steps!.SelectMany(id => result[id].RequiredCapabilities).Distinct().ToArray();
            definition.Available = template.UnavailableReason is null && template.Steps!.All(id => result[id].Available);
            definition.RequiresApproval = template.Steps!.Any(id => result[id].RequiresApproval);
            definition.UnavailableReason = definition.Available ? null : template.UnavailableReason ??
                "A required workflow skill is unavailable: " + template.Steps!.First(id => !result[id].Available);
            definition.InputSchema = WorkflowSchema;
            result.Add(template.Id, definition);
        }
        return result.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }

    internal static string ToolKey(McpSkillTemplate template) =>
        "google:" + (template.Tool!.StartsWith("calendar_", StringComparison.Ordinal) ? "calendar" :
            template.Tool.StartsWith("gmail_", StringComparison.Ordinal) ? "gmail" : "meet") + "/" + template.Tool;

    private static bool HasScope(McpConnectionInfo connection, McpSkillTemplate template)
    {
        var granted = connection.GrantedScopes;
        // Some gateway tokens do not report Google scopes. In that case the gateway
        // remains authoritative and rejects a missing grant at tool execution time.
        if (!granted.Any(s => s.StartsWith("https://www.googleapis.com/auth/", StringComparison.Ordinal))) return true;
        var service = template.Tool!.Split('_')[0];
        var expected = service switch
        {
            "gmail" => new[] { "https://www.googleapis.com/auth/gmail.modify" },
            "calendar" when template.Permission == SkillPermissionLevel.Read =>
                ["https://www.googleapis.com/auth/calendar.readonly", "https://www.googleapis.com/auth/calendar.events"],
            "calendar" => ["https://www.googleapis.com/auth/calendar.events"],
            "meet" when template.Permission == SkillPermissionLevel.Read =>
                ["https://www.googleapis.com/auth/meetings.space.readonly", "https://www.googleapis.com/auth/meetings.space.created"],
            "meet" => ["https://www.googleapis.com/auth/meetings.space.created"],
            _ => Array.Empty<string>()
        };
        return expected.Length == 0 || expected.Any(granted.Contains);
    }

    private static McpSkillDefinition Definition(McpSkillTemplate item) => new()
    {
        Id = item.Id, Name = string.Join(' ', item.Id.Split('.').Last().Split('_').Select(s => char.ToUpperInvariant(s[0]) + s[1..])),
        Description = item.UnavailableReason ?? (item.Mode == "workflow"
            ? "Execute these ordered skills with fully resolved arguments: " + string.Join(", ", item.Steps!)
            : "Execute " + item.Id + " using the connected Google MCP service."),
        Service = item.Service, PermissionLevel = item.Permission,
        RequiredCapabilities = item.Tool is null ? [] : [item.Tool],
        InputSchema = EmptySchema, OutputSchema = ResultSchema,
        RequiresApproval = item.Permission == SkillPermissionLevel.Destructive,
        UnavailableReason = item.UnavailableReason ?? "No connected Google account."
    };

    private static JsonElement InputSchema(McpSkillTemplate template, McpToolInfo tool)
    {
        if (template.AllowedArguments is null && template.Mode is "direct" or "create_meet") return tool.InputSchema;
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (tool.InputSchema.TryGetProperty("properties", out var original))
            foreach (var property in original.EnumerateObject())
                if (template.AllowedArguments is null || template.AllowedArguments.Contains(property.Name))
                    properties[property.Name] = property.Value.Clone();
        if (template.Mode == "free_slots")
            properties["durationMinutes"] = JsonSerializer.SerializeToElement(new { type = "integer", minimum = 5, maximum = 480 });
        if (template.Mode == "label_change")
            properties["labelId"] = JsonSerializer.SerializeToElement(new { type = "string" });
        if (template.Mode == "relative_range")
            properties["timeZone"] = JsonSerializer.SerializeToElement(new { type = "string", description = "IANA time zone, e.g. Europe/London" });
        if (template.Mode == "delete_event")
            properties["targetScope"] = JsonSerializer.SerializeToElement(new { type = "string", @enum = new[] { "occurrence", "series" } });
        if (template.Id == "gmail.get_from_sender")
            properties["sender"] = JsonSerializer.SerializeToElement(new { type = "string" });
        var required = tool.InputSchema.TryGetProperty("required", out var originalRequired)
            ? originalRequired.EnumerateArray().Select(x => x.GetString()!).Where(properties.ContainsKey).ToList() : [];
        if (template.Mode == "label_change" && template.Id is "gmail.add_label" or "gmail.remove_label") required.Add("labelId");
        if (template.Mode == "free_slots") required.Add("durationMinutes");
        if (template.Mode == "relative_range") required.Add("timeZone");
        if (template.Mode == "delete_event") required.Add("targetScope");
        if (template.Id == "gmail.get_from_sender") required.Add("sender");
        if (template.Id.StartsWith("gmail.") && template.Mode == "search_filter" && template.Id != "gmail.search_messages") required.Remove("query");
        return JsonSerializer.SerializeToElement(new { type = "object", properties, required, additionalProperties = false });
    }
}
