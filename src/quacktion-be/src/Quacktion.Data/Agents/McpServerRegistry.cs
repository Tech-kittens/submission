using Microsoft.Extensions.Options;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public sealed class McpSettings
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "https://mcp.adriantd.com/";
    public Dictionary<string, McpProviderSettings> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Google"] = new() { BasePath = "api/google/", Services = new(StringComparer.OrdinalIgnoreCase)
        { ["Calendar"] = "calendar", ["Gmail"] = "gmail", ["Meet"] = "meet" } },
        ["GitHub"] = new() { BasePath = "api/github/", Services = new(StringComparer.OrdinalIgnoreCase)
        { ["GitHub"] = "" } }
    };
    // Gateway homepage advertises /mcp; this suffix was confirmed by the operator.
    public string TransportSuffix { get; set; } = "mcp";
    public string OAuthClientId { get; set; } = "";
    public string OAuthCallbackUrl { get; set; } = "";
    public string[] OAuthScopes { get; set; } = [];
    public string CredentialEncryptionKey { get; set; } = "";
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int ToolTimeoutSeconds { get; set; } = 60;
    public Dictionary<string, string[]> ReadOnlyTools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class McpProviderSettings
{
    public string BasePath { get; set; } = "";
    public Dictionary<string, string> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed record McpServerDefinition(string Id, string Provider, string Service, Uri Endpoint, string[] ReadOnlyTools);
public interface IMcpServerRegistry
{
    IReadOnlyList<McpServerDefinition> All();
    McpServerDefinition Get(string serverId);
    Uri BaseUri { get; }
}
public sealed class McpServerRegistry(IOptions<McpSettings> options) : IMcpServerRegistry
{
    public Uri BaseUri
    {
        get
        {
            if (!Uri.TryCreate(options.Value.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                || uri.IsLoopback || uri.HostNameType != UriHostNameType.Dns || uri.Port != 443
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
                throw new AppException(503, "mcp_base_url_invalid");
            return uri;
        }
    }
    public IReadOnlyList<McpServerDefinition> All()
    {
        var result = new List<McpServerDefinition>();
        foreach (var (provider, config) in options.Value.Providers)
            foreach (var (service, path) in config.Services)
            {
                var id = provider.ToLowerInvariant() + ":" + service.ToLowerInvariant();
                var segments = new[] { config.BasePath, path, options.Value.TransportSuffix };
                if (segments.Any(x => x.Contains("..", StringComparison.Ordinal) || x.Contains('%') || x.Contains('\\')
                    || x.Contains(':') || x.Contains('?') || x.Contains('#') || x.StartsWith('/')))
                    throw new AppException(503, "mcp_service_path_invalid");
                var relative = string.Join('/', segments.Select(x => x.Trim('/')).Where(x => x.Length > 0));
                var uri = new Uri(BaseUri, relative);
                if (uri.Authority != BaseUri.Authority) throw new AppException(503, "mcp_origin_mismatch");
                result.Add(new(id, provider.ToLowerInvariant(), service.ToLowerInvariant(), uri,
                    options.Value.ReadOnlyTools.GetValueOrDefault(id) ?? []));
            }
        if (result.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new AppException(503, "mcp_duplicate_service");
        return result;
    }
    public McpServerDefinition Get(string serverId) => All().SingleOrDefault(x => x.Id == serverId)
        ?? throw new AppException(404, "mcp_service_not_found");
}
