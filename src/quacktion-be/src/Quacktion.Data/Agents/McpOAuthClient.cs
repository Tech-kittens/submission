using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public sealed record McpOAuthMetadata(string Resource, Uri AuthorizationEndpoint, Uri TokenEndpoint,
    Uri? RegistrationEndpoint, Uri? RevocationEndpoint, bool RefreshSupported);
public sealed record McpOAuthTokens(string AccessToken, string? RefreshToken, DateTime? ExpiresAt, string[] Scopes);

public sealed class McpOAuthClient(IHttpClientFactory clients, IMcpServerRegistry registry, IOptions<McpSettings> options)
{
    public async Task<McpOAuthMetadata> DiscoverAsync(CancellationToken ct)
    {
        using var resource = await GetAsync(new(registry.BaseUri, ".well-known/oauth-protected-resource"), ct);
        var root = resource.RootElement;
        var resourceId = root.GetProperty("resource").GetString()!;
        RequireOrigin(resourceId);
        if (!root.GetProperty("authorization_servers").EnumerateArray().Any(x => SameOrigin(x.GetString())))
            throw new AppException(502, "mcp_oauth_issuer_not_trusted");
        using var metadata = await GetAsync(new(registry.BaseUri, ".well-known/oauth-authorization-server"), ct);
        var m = metadata.RootElement;
        RequireOrigin(m.GetProperty("issuer").GetString()!);
        if (!Has(m, "code_challenge_methods_supported", "S256") || !Has(m, "token_endpoint_auth_methods_supported", "none")
            || !Has(m, "response_types_supported", "code")) throw new AppException(502, "mcp_oauth_contract_unsupported");
        return new(resourceId, Endpoint(m, "authorization_endpoint")!, Endpoint(m, "token_endpoint")!,
            Endpoint(m, "registration_endpoint", optional: true), Endpoint(m, "revocation_endpoint", optional: true),
            Has(m, "grant_types_supported", "refresh_token"));
    }
    public async Task<string> RegisterAsync(McpOAuthMetadata metadata, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.Value.OAuthClientId)) return options.Value.OAuthClientId;
        if (metadata.RegistrationEndpoint is null) throw new AppException(503, "mcp_oauth_client_id_required");
        using var response = await clients.CreateClient("McpOAuth").PostAsJsonAsync(metadata.RegistrationEndpoint, new
        {
            client_name = "Quacktion", redirect_uris = new[] { options.Value.OAuthCallbackUrl },
            grant_types = new[] { "authorization_code" }, response_types = new[] { "code" }, token_endpoint_auth_method = "none"
        }, ct);
        using var json = await ReadResponseAsync(response, ct);
        var id = json.RootElement.GetProperty("client_id").GetString();
        if (id is not { Length: > 0 and <= 512 }) throw new AppException(502, "mcp_registration_invalid");
        return id;
    }
    public async Task<McpOAuthTokens> ExchangeAsync(Uri endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await clients.CreateClient("McpOAuth").PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        using var json = await ReadResponseAsync(response, ct);
        var root = json.RootElement;
        if (!root.TryGetProperty("token_type", out var type) || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase))
            throw new AppException(502, "mcp_token_type_unsupported");
        var token = root.GetProperty("access_token").GetString();
        if (token is not { Length: > 0 and <= 16384 }) throw new AppException(502, "mcp_token_invalid");
        DateTime? expires = null;
        if (root.TryGetProperty("expires_in", out var expiry))
        {
            if (!expiry.TryGetInt32(out var seconds) || seconds <= 0) throw new AppException(502, "mcp_token_expiry_invalid");
            expires = DateTime.UtcNow.AddSeconds(seconds);
        }
        var refresh = root.TryGetProperty("refresh_token", out var value) ? value.GetString() : null;
        var scopes = root.TryGetProperty("scope", out var scope) ? scope.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [] : [];
        return new(token, refresh, expires, scopes);
    }
    public async Task RevokeAsync(Uri endpoint, string token, CancellationToken ct)
    {
        using var response = await clients.CreateClient("McpOAuth").PostAsync(endpoint,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), ct);
        if (!response.IsSuccessStatusCode) throw new AppException(502, "mcp_revocation_failed");
    }
    private async Task<JsonDocument> GetAsync(Uri uri, CancellationToken ct)
    {
        using var response = await clients.CreateClient("McpOAuth").GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        return await ReadResponseAsync(response, ct);
    }
    internal static async Task<JsonDocument> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode) throw new AppException(502, response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "mcp_rate_limited" : "mcp_oauth_failed");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) != 0)
        {
            if (buffer.Length + count > 65536) throw new AppException(502, "mcp_oauth_response_too_large");
            buffer.Write(chunk, 0, count);
        }
        return JsonDocument.Parse(buffer.ToArray());
    }
    private Uri? Endpoint(JsonElement metadata, string name, bool optional = false)
    {
        if (optional && !metadata.TryGetProperty(name, out _)) return null;
        var endpoint = metadata.GetProperty(name).GetString()!;
        RequireOrigin(endpoint);
        return new(endpoint);
    }
    private bool SameOrigin(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && uri.Authority == registry.BaseUri.Authority && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private void RequireOrigin(string value)
    { if (!SameOrigin(value)) throw new AppException(502, "mcp_oauth_endpoint_not_trusted"); }
    private static bool Has(JsonElement m, string key, string value) => m.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
        && a.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == value);
}
