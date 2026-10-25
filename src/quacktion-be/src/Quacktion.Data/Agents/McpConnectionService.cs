using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;

namespace Quacktion.Data.Agents;

public sealed record ProtectedMcpOperation(string ConnectionId, string ServerId, string ToolName,
    JsonElement Arguments, string SchemaDigest, string ParentTaskId, string? SkillId = null,
    string? ApprovalContext = null);

public sealed class McpConnectionService(
    IUserMcpConnectionRepo connections, IMcpCredentialRepo credentials, IMcpOAuthTransactionRepo transactions,
    IMcpExecutionAuditRepo audits, IAccountRepo accounts, ISessionRepo sessions,
    IQuacktionRepo tasks, IQuacktionService actions, ISessionEvents events,
    IMcpClientGateway mcp, IMcpServerRegistry registry, McpOAuthClient oauth,
    IOptions<McpSettings> options, ILogger<McpConnectionService> logger) : IMcpConnectionService, IMcpToolGateway
{
    private sealed record StoredTokens(McpOAuthTokens Tokens, string ClientId);
    private McpSettings Settings => options.Value;

    public async Task<IReadOnlyList<McpConnectionInfo>> ListAsync(string owner, CancellationToken ct) =>
        (await connections.ListAsync(x => x.UserId == owner, 0, 100, ct)).Select(ToInfo).ToArray();
    public IReadOnlyList<McpProviderInfo> Providers() => registry.All().GroupBy(x => x.Provider)
        .Select(x => new McpProviderInfo(x.Key, x.Key, Settings.OAuthScopes)).ToArray();

    public async Task<Uri> BeginConnectAsync(string owner, string provider, CancellationToken ct)
    {
        Guard.State(Settings.Enabled, "mcp_not_enabled");
        await RequireAccount(owner, ct);
        var services = registry.All().Where(x => x.Provider == provider.ToLowerInvariant()).Select(x => x.Id).ToList();
        if (services.Count == 0) throw new AppException(404, "mcp_provider_not_found");
        if (!Uri.TryCreate(Settings.OAuthCallbackUrl, UriKind.Absolute, out var callback) || callback.Scheme != "https")
            throw new AppException(503, "mcp_oauth_callback_invalid");
        _ = EncryptionKey();
        var metadata = await oauth.DiscoverAsync(ct);
        var clientId = await oauth.RegisterAsync(metadata, ct);
        var state = Secrets.New();
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        await transactions.InsertAsync(new McpOAuthTransaction
        {
            Id = Secrets.Hash(state), UserId = owner, Provider = provider.ToLowerInvariant(),
            CodeVerifier = Seal(Encoding.UTF8.GetBytes(verifier), Secrets.Hash(state)), ClientId = clientId, Resource = metadata.Resource, EnabledServices = services,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        }, ct);
        var query = new Dictionary<string, string>
        {
            ["provider"] = provider.ToLowerInvariant(),
            ["client_id"] = clientId, ["redirect_uri"] = Settings.OAuthCallbackUrl, ["response_type"] = "code",
            ["state"] = state, ["resource"] = metadata.Resource, ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        };
        if (Settings.OAuthScopes.Length > 0) query["scope"] = string.Join(' ', Settings.OAuthScopes);
        var authorizationEndpoint = metadata.AuthorizationEndpoint;
        var builder = new UriBuilder(authorizationEndpoint);
        var encoded = await new FormUrlEncodedContent(query).ReadAsStringAsync(ct);
        builder.Query = string.IsNullOrEmpty(builder.Query) ? encoded : builder.Query.TrimStart('?') + "&" + encoded;
        return builder.Uri;
    }
    public async Task<McpConnectionInfo> CompleteConnectAsync(string code, string state, CancellationToken ct)
    {
        Guard.Input(code.Length is > 0 and <= 4096 && state.Length is > 0 and <= 256, "invalid_oauth_callback");
        var tx = await transactions.GetAsync(Secrets.Hash(state), ct);
        Guard.State(tx is not null && tx.ExpiresAt > DateTime.UtcNow && tx.ClientId.Length > 0, "oauth_state_expired");
        await RequireAccount(tx!.UserId, ct);
        Guard.State(await transactions.DeleteAsync(tx, ct), "oauth_state_replayed");
        var metadata = await oauth.DiscoverAsync(ct);
        Guard.State(metadata.Resource == tx.Resource, "mcp_oauth_resource_changed");
        var token = await oauth.ExchangeAsync(metadata.TokenEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = tx.ClientId, ["redirect_uri"] = Settings.OAuthCallbackUrl,
            ["code"] = code, ["code_verifier"] = Encoding.UTF8.GetString(Open(tx.CodeVerifier, tx.Id)), ["grant_type"] = "authorization_code", ["resource"] = tx.Resource
        }, ct);
        var id = Guid.NewGuid().ToString("N");
        // The issuer advertises no userinfo/account identity endpoint. Do not decode an
        // unverified JWT or pretend a grant ID identifies a Google account.
        var connection = new UserMcpConnection
        {
            Id = id, UserId = tx.UserId, Provider = tx.Provider, ProviderAccountId = "grant:" + id,
            ServerId = tx.Provider, EnabledServices = tx.EnabledServices, CredentialReference = id,
            CredentialKind = "mcp-oauth", DisplayName = tx.Provider + " connection " + id[..6], GrantedScopes = [.. token.Scopes]
        };
        var credential = new McpCredential
        {
            Id = id, UserId = tx.UserId, ConnectionId = id,
            EncryptedTokens = Protect(new(token, tx.ClientId), id), AccessTokenExpiresAt = token.ExpiresAt ?? DateTime.MaxValue
        };
        await credentials.InsertAsync(credential, ct);
        try { await connections.InsertAsync(connection, ct); }
        catch { await credentials.DeleteAsync(credential, ct); throw; }
        logger.LogInformation("McpAccountConnected {UserId} {ConnectionId}", tx.UserId, id);
        return ToInfo(connection);
    }
    public async Task DisconnectAsync(string owner, string connectionId, CancellationToken ct)
    {
        var connection = await connections.FindAsync(x => x.Id == connectionId && x.UserId == owner, ct);
        if (connection is null) return;
        connection.Status = "Disconnected";
        connection.EnabledTools.Clear();
        await Guard.Save(connections, connection, ct); // Revoke local access before attempting remote cleanup.
        var credential = await credentials.FindAsync(x => x.ConnectionId == connectionId && x.UserId == owner, ct);
        if (credential is null) return;
        try
        {
            if (connection.CredentialKind == "mcp-oauth")
            {
                var metadata = await oauth.DiscoverAsync(ct);
                if (metadata.RevocationEndpoint is { } endpoint)
                {
                    var token = Unprotect(credential.EncryptedTokens, connectionId).Tokens;
                    await oauth.RevokeAsync(endpoint, token.RefreshToken ?? token.AccessToken, ct);
                }
            }
        }
        catch (Exception e) when (e is AppException or HttpRequestException or JsonException or CryptographicException)
        { logger.LogWarning("McpRemoteRevocationUnavailable {ConnectionId}", connectionId); }
        finally { await credentials.DeleteAsync(credential, CancellationToken.None); }
    }
    public async Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(string owner, string connectionId, CancellationToken ct)
    {
        var connection = await GetConnection(owner, connectionId, ct);
        var token = await GetAccessToken(connection, ct);
        var result = new List<McpToolInfo>();
        foreach (var service in connection.EnabledServices)
        {
            var server = registry.Get(service);
            Guard.State(server.Provider == connection.Provider, "mcp_provider_mismatch");
            foreach (var tool in await mcp.DiscoverAsync(service, token, ct))
                result.Add(new(tool.Name, tool.Description, tool.InputSchema, tool.ReadOnly, tool.Destructive, service, tool.SchemaDigest));
        }
        return result;
    }
    public async Task<McpConnectionInfo> SetEnabledToolsAsync(string owner, string connectionId, string[] toolNames, CancellationToken ct)
    {
        Guard.Input(toolNames.Length <= 100 && toolNames.Distinct(StringComparer.Ordinal).Count() == toolNames.Length, "invalid_mcp_tool_selection");
        var connection = await GetConnection(owner, connectionId, ct);
        var tools = await ListToolsAsync(owner, connectionId, ct);
        Guard.Input(toolNames.All(x => tools.Any(t => ToolKey(t.ServerId, t.Name) == x)), "unknown_mcp_tool");
        connection.EnabledTools = [.. toolNames];
        connection.UpdatedAt = DateTime.UtcNow;
        await Guard.Save(connections, connection, ct);
        return ToInfo(connection);
    }
    public async Task HealthAsync(string owner, string connectionId, CancellationToken ct)
    {
        var connection = await GetConnection(owner, connectionId, ct);
        var token = await GetAccessToken(connection, ct);
        foreach (var service in connection.EnabledServices) await mcp.HealthAsync(service, token, ct);
    }
    // Only the authenticated frontend can select an external account for a voice request.
    // Rebind before approval; any previous digest/approval becomes invalid.
    public async Task<Quacktion.App.Models.Quacktion> BindTaskAsync(string owner, string connectionId, string taskId, string[] services, CancellationToken ct)
    {
        var connection = await GetConnection(owner, connectionId, ct);
        Guard.Input(services.Length > 0 && services.Distinct().Count() == services.Length && services.All(connection.EnabledServices.Contains), "mcp_services_invalid");
        var task = await actions.GetAsync(owner, taskId, ct);
        Guard.State(task.ActionType is "mcp.request" or "internal.mcp_read" && task.Status == QuacktionStatus.PendingApproval, "mcp_task_not_bindable");
        using var args = JsonDocument.Parse(task.ParametersJson);
        var query = args.RootElement.GetProperty("query").GetString();
        task.ParametersJson = JsonSerializer.Serialize(new { query, connectionId, services, announcementPolicy = task.AnnouncementPolicy });
        task.ActionDigest = Secrets.Digest(task.ActionType, task.ParametersJson);
        task.AutoAuthorizedMcp = task.ActionType is "mcp.request" or "internal.mcp_read";
        if (task.AutoAuthorizedMcp)
        {
            task.Approval = null;
            task.Status = QuacktionStatus.Queued;
        }
        else task.Approval = new() { QuacktionId = task.Id, OwnerAccountId = owner,
            ActionDigest = task.ActionDigest, ExpiresAt = DateTime.UtcNow.AddMinutes(30) };
        await Guard.Save(tasks, task, ct);
        await events.PublishAsync(owner, task.SessionId, task.AutoAuthorizedMcp ? "QuacktionStatusChanged" : "ApprovalRequested", task, ct);
        return task;
    }
    // Background reads require persisted user opt-in as well as existing connection/tool permissions.
    public async Task<JsonElement> ExecutePreparationReadAsync(string owner, string connectionId, string serverId,
        string toolName, JsonElement arguments, string[] requiredScopes, CancellationToken ct)
    {
        await RequireAccount(owner, ct);
        var connection = await GetConnection(owner, connectionId, ct);
        CheckTool(connection, serverId, toolName);
        Guard.State(requiredScopes.Length > 0 && requiredScopes.All(connection.GrantedScopes.Contains), "preparation_oauth_scope_missing");
        var token = await GetAccessToken(connection, ct);
        var tool = (await mcp.DiscoverAsync(serverId, token, ct)).SingleOrDefault(x => x.Name == toolName && x.ReadOnly)
            ?? throw new AppException(403, "preparation_read_tool_required");
        McpClientGateway.ValidateArguments(tool, arguments);
        CheckTool(await GetConnection(owner, connectionId, ct), serverId, toolName);
        var audit = new McpExecutionAudit { UserId = owner, ConnectionId = connectionId, ServerId = serverId,
            ToolName = toolName, ArgumentsDigest = Secrets.Digest(toolName, arguments.GetRawText()), Status = "Started" };
        await audits.InsertAsync(audit, ct);
        return await InvokeAndAudit(audit, token, tool.SchemaDigest, arguments, false, ct);
    }

    public Task<JsonElement> ExecuteReadOnlyAsync(McpToolInvocation invocation, CancellationToken ct) => ExecuteOrProposeAsync(invocation, ct);
    public async Task<JsonElement> ExecuteOrProposeAsync(McpToolInvocation invocation, CancellationToken ct)
    {
        await AuthorizeParent(invocation.OwnerAccountId, invocation.SessionId, invocation.ParentTaskId, invocation.ConnectionId, ct, invocation.ServerId);
        var connection = await GetConnection(invocation.OwnerAccountId, invocation.ConnectionId, ct);
        CheckTool(connection, invocation.ServerId, invocation.ToolName);
        var token = await GetAccessToken(connection, ct);
        var tool = (await mcp.DiscoverAsync(invocation.ServerId, token, ct)).SingleOrDefault(x => x.Name == invocation.ToolName)
            ?? throw new AppException(400, "mcp_unknown_tool");
        McpClientGateway.ValidateArguments(tool, invocation.Arguments);
        McpSkillTemplate? skill = null;
        if (invocation.SkillId is not null)
        {
            Guard.State(McpSkillCatalog.All.TryGetValue(invocation.SkillId, out skill)
                && skill.Tool == invocation.ToolName && McpSkillRegistry.ToolKey(skill) ==
                    invocation.ServerId + "/" + invocation.ToolName, "mcp_skill_tool_mismatch");
        }
        var needsApproval = invocation.ForceApproval || skill?.Permission == SkillPermissionLevel.Destructive
            || skill is null && !tool.ReadOnly;
        if (needsApproval)
        {
            var operation = new ProtectedMcpOperation(connection.Id, invocation.ServerId, tool.Name, invocation.Arguments,
                tool.SchemaDigest, invocation.ParentTaskId, invocation.SkillId, invocation.ApprovalContext);
            var parameters = JsonSerializer.Serialize(operation);
            var child = await actions.ProposeAsync(invocation.OwnerAccountId, invocation.SessionId,
                new ProposalRequest("mcp.invoke", parameters, "mcp:" + Secrets.Digest(invocation.ParentTaskId, parameters)), ct);
            return JsonSerializer.SerializeToElement(new { status = "awaiting_approval", quacktionId = child.Id, completed = false });
        }
        await AuthorizeParent(invocation.OwnerAccountId, invocation.SessionId, invocation.ParentTaskId, connection.Id, ct, invocation.ServerId);
        var audit = new McpExecutionAudit
        {
            Id = skill is { Permission: not SkillPermissionLevel.Read }
                ? Secrets.Digest("mcp-operation", invocation.ParentTaskId + ":" + skill.Id + ":" +
                    invocation.Arguments.GetRawText()) : Guid.NewGuid().ToString("N"),
            UserId = connection.UserId, SessionId = invocation.SessionId, ConnectionId = connection.Id,
            ServerId = invocation.ServerId, ToolName = tool.Name, ArgumentsDigest = Secrets.Digest(tool.Name, invocation.Arguments.GetRawText()), Status = "Started"
        };
        try { await audits.InsertAsync(audit, ct); }
        catch (AppException e) when (e.Code == "duplicate_key")
        { throw new AppException(409, "mcp_execution_already_attempted_reconcile_required"); }
        // This is a reviewed application skill, not an agent-supplied approval bit.
        var authorized = skill is not null || tool.ReadOnly;
        return await InvokeAndAudit(audit, token, tool.SchemaDigest, invocation.Arguments, authorized, ct);
    }
    public async Task<AgentTaskResult> ExecuteApprovedAsync(AgentTaskRequest request, CancellationToken ct)
    {
        var task = await tasks.FindAsync(x => x.IdempotencyKey == request.IdempotencyKey && x.OwnerAccountId == request.OwnerAccountId && x.SessionId == request.SessionId, ct);
        Guard.State(task is not null && task.ActionType == "mcp.invoke" && task.Status == QuacktionStatus.InProgress
            && !task.CancellationRequested && task.LeaseExpiresAt > DateTime.UtcNow && QuacktionService.ValidApproval(task), "approval_required");
        var operation = JsonSerializer.Deserialize<ProtectedMcpOperation>(task!.ParametersJson) ?? throw new AppException(400, "mcp_operation_invalid");
        await AuthorizeParent(request.OwnerAccountId, request.SessionId, operation.ParentTaskId, operation.ConnectionId, ct, operation.ServerId);
        var connection = await GetConnection(request.OwnerAccountId, operation.ConnectionId, ct);
        CheckTool(connection, operation.ServerId, operation.ToolName);
        var token = await GetAccessToken(connection, ct);
        // A unique durable execution marker fences retries, expired leases, and repeated approvals.
        var audit = new McpExecutionAudit
        {
            Id = Secrets.Digest("mcp-operation", task.Id), UserId = request.OwnerAccountId, SessionId = request.SessionId,
            ConnectionId = connection.Id, ServerId = operation.ServerId, ToolName = operation.ToolName,
            ArgumentsDigest = task.ActionDigest, Status = "Started"
        };
        try { await audits.InsertAsync(audit, ct); }
        catch (AppException e) when (e.Code == "duplicate_key") { return new(false, null, "mcp_execution_already_attempted_reconcile_required"); }
        // Revalidate after token refresh and before external I/O.
        var latest = await tasks.GetAsync(task.Id, ct);
        Guard.State(latest is not null && latest.LeaseId == task.LeaseId && latest.LeaseExpiresAt > DateTime.UtcNow
            && latest.ParametersJson == task.ParametersJson && !latest.CancellationRequested && QuacktionService.ValidApproval(latest), "approval_invalid");
        CheckTool(await GetConnection(request.OwnerAccountId, connection.Id, ct), operation.ServerId, operation.ToolName);
        await AuthorizeParent(request.OwnerAccountId, request.SessionId, operation.ParentTaskId, connection.Id, ct, operation.ServerId);
        var result = await InvokeAndAudit(audit, token, operation.SchemaDigest, operation.Arguments, true, ct);
        return new(true, result.GetRawText(), null);
    }
    private async Task<JsonElement> InvokeAndAudit(McpExecutionAudit audit, string token, string schema, JsonElement args, bool approved, CancellationToken ct)
    {
        try
        {
            var result = await mcp.ExecuteAsync(audit.ServerId, token, audit.ToolName,
                args.EnumerateObject().ToDictionary(x => x.Name, x => (object?)x.Value.Clone()), schema, approved, ct);
            audit.Status = "Succeeded";
            await Guard.Save(audits, audit, ct);
            return result;
        }
        catch (Exception e)
        {
            audit.Status = approved ? "FailedOrOutcomeUnknown" : "Failed";
            audit.FailureCode = e is AppException app ? app.Code : e is OperationCanceledException ? "mcp_cancelled_or_timeout" : "mcp_execution_failed";
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await audits.ReplaceAsync(audit, cleanup.Token);
            logger.LogWarning("McpToolFailed {SessionId} {ConnectionId} {ServerId} {ToolName} {FailureCode}", audit.SessionId, audit.ConnectionId, audit.ServerId, audit.ToolName, audit.FailureCode);
            throw;
        }
    }
    private async Task AuthorizeParent(string owner, string sessionId, string parentId, string connectionId, CancellationToken ct, string serverId)
    {
        Guard.State(Settings.Enabled, "mcp_not_enabled");
        await RequireAccount(owner, ct);
        var session = await sessions.FindAsync(x => x.Id == sessionId && x.OwnerAccountId == owner, ct);
        Guard.State(session?.Status is SessionStatus.Active or SessionStatus.Suspended, "session_not_active");
        var parent = await tasks.FindAsync(x => x.Id == parentId && x.OwnerAccountId == owner && x.SessionId == sessionId, ct);
        Guard.State(parent is not null && parent.ActionType is "mcp.request" or "internal.mcp_read" && !parent.CancellationRequested
            && parent.Status is QuacktionStatus.InProgress or QuacktionStatus.Completed or QuacktionStatus.WaitingForTools
            && (QuacktionService.ValidApproval(parent) || QuacktionService.AuthorizedMcp(parent)), "mcp_request_confirmation_required");
        using var parameters = JsonDocument.Parse(parent!.ParametersJson);
        Guard.State(parameters.RootElement.TryGetProperty("connectionId", out var id) && id.GetString() == connectionId, "mcp_account_not_selected");
        Guard.State(parameters.RootElement.TryGetProperty("services", out var allowed) && allowed.ValueKind == JsonValueKind.Array
            && allowed.EnumerateArray().Any(x => x.GetString() == serverId), "mcp_service_not_authorized_for_request");
    }
    private void CheckTool(UserMcpConnection connection, string serverId, string tool)
    {
        Guard.State(connection.EnabledServices.Contains(serverId) && registry.Get(serverId).Provider == connection.Provider, "mcp_service_disabled");
        Guard.State(connection.EnabledTools.Contains(ToolKey(serverId, tool), StringComparer.Ordinal), "mcp_tool_disabled");
    }
    public static string ToolKey(string serverId, string tool) => serverId + "/" + tool;
    public async Task<string> GetAccessToken(string owner, string id, CancellationToken ct) => await GetAccessToken(await GetConnection(owner, id, ct), ct);
    private async Task<string> GetAccessToken(UserMcpConnection connection, CancellationToken ct)
    {
        var credential = await credentials.FindAsync(x => x.ConnectionId == connection.Id && x.UserId == connection.UserId, ct)
            ?? throw new AppException(409, "mcp_reauthorization_required");
        var stored = Unprotect(credential.EncryptedTokens, connection.Id);
        var token = stored.Tokens;
        if (token.ExpiresAt is null || token.ExpiresAt > DateTime.UtcNow.AddSeconds(30)) return token.AccessToken;
        var metadata = await oauth.DiscoverAsync(ct);
        if (metadata.RefreshSupported && !string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            var refreshed = await oauth.ExchangeAsync(metadata.TokenEndpoint, new Dictionary<string, string>
            { ["client_id"] = stored.ClientId, ["grant_type"] = "refresh_token", ["refresh_token"] = token.RefreshToken, ["resource"] = metadata.Resource }, ct);
            refreshed = refreshed with { RefreshToken = refreshed.RefreshToken ?? token.RefreshToken };
            credential.EncryptedTokens = Protect(new(refreshed, stored.ClientId), connection.Id);
            credential.AccessTokenExpiresAt = refreshed.ExpiresAt ?? DateTime.MaxValue;
            credential.UpdatedAt = DateTime.UtcNow;
            if (!await credentials.ReplaceAsync(credential, ct)) throw new AppException(409, "mcp_refresh_conflict");
            return refreshed.AccessToken;
        }
        connection.Status = "ReauthorizationRequired";
        await Guard.Save(connections, connection, ct);
        throw new AppException(409, "mcp_reauthorization_required");
    }
    private async Task<UserMcpConnection> GetConnection(string owner, string id, CancellationToken ct)
    {
        Guard.State(Settings.Enabled, "mcp_not_enabled");
        await RequireAccount(owner, ct);
        var connection = await connections.FindAsync(x => x.Id == id && x.UserId == owner, ct) ?? throw new AppException(404, "not_found");
        Guard.State(connection.Status == "Connected" && connection.CredentialKind == "mcp-oauth", "mcp_reauthorization_required");
        return connection;
    }
    private async Task RequireAccount(string owner, CancellationToken ct) =>
        Guard.State(await accounts.GetAsync(owner, ct) is { IsActive: true }, "account_inactive");
    private string Protect(StoredTokens token, string id) => Seal(JsonSerializer.SerializeToUtf8Bytes(token), id);
    private string Seal(byte[] bytes, string id)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[bytes.Length];
        try { using var aes = new AesGcm(EncryptionKey(), 16); aes.Encrypt(nonce, bytes, cipher, tag, Encoding.UTF8.GetBytes(id)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }
    private StoredTokens Unprotect(string value, string id)
    {
        var plain = Open(value, id);
        try { return JsonSerializer.Deserialize<StoredTokens>(plain) ?? throw new CryptographicException("Invalid credential."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private byte[] Open(string value, string id)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < 29) throw new CryptographicException("Invalid credential.");
        var plain = new byte[bytes.Length - 28];
        try
        {
            using var aes = new AesGcm(EncryptionKey(), 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(id));
            return plain;
        }
        catch { CryptographicOperations.ZeroMemory(plain); throw; }
    }
    private byte[] EncryptionKey()
    {
        try { var key = Convert.FromBase64String(Settings.CredentialEncryptionKey); if (key.Length == 32) return key; }
        catch (FormatException) { }
        throw new AppException(503, "mcp_encryption_not_configured");
    }
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static McpConnectionInfo ToInfo(UserMcpConnection c) => new(c.Id, c.Provider, c.ProviderAccountId, c.ServerId, c.DisplayName,
        c.GrantedScopes, c.EnabledTools, c.Status, c.CreatedAt, c.EnabledServices, c.AccountIdentityVerified);
}

public sealed class McpOperationGateway(McpConnectionService service) : IAgentGateway
{
    public string Provider => "mcp";
    public bool Supports(string capability) => capability == "mcp.invoke";
    public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct) => service.ExecuteApprovedAsync(request, ct);
}
