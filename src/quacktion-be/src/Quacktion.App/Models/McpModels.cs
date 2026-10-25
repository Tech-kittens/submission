namespace Quacktion.App.Models;

public sealed class UserMcpConnection : Entity
{
    public string UserId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string ProviderAccountId { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<string> GrantedScopes { get; set; } = [];
    public List<string> EnabledTools { get; set; } = [];
    public List<string> EnabledServices { get; set; } = [];
    public string CredentialKind { get; set; } = "legacy-google";
    public bool AccountIdentityVerified { get; set; }
    public string CredentialReference { get; set; } = "";
    public string Status { get; set; } = "Connected";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Tokens are kept in a separate document and encrypted before persistence.
public sealed class McpCredential : Entity
{
    public string UserId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string EncryptedTokens { get; set; } = "";
    public DateTime AccessTokenExpiresAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class McpOAuthTransaction : Entity
{
    public string UserId { get; set; } = "";
    public string Provider { get; set; } = "";
    public string CodeVerifier { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Resource { get; set; } = "";
    public List<string> EnabledServices { get; set; } = [];
    public DateTime ExpiresAt { get; set; }
}

public sealed class McpExecutionAudit : Entity
{
    public string UserId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string ConnectionId { get; set; } = "";
    public string ServerId { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string ArgumentsDigest { get; set; } = "";
    public string Status { get; set; } = "";
    public string? FailureCode { get; set; }
}

public sealed class McpServerRegistration : Entity
{
    public string Provider { get; set; } = "";
    public string Service { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
