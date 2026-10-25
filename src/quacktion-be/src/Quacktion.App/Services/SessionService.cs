using Microsoft.Extensions.Logging;
using System.Text.Json;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
namespace Quacktion.App.Services;

public sealed class SessionService(
    ISessionRepo sessions,
    IDeviceRepo devices,
    IConversationalAgentGateway voice,
    IDeviceConnectionManager connections,
    ISessionEvents events,
    ILogger<SessionService> logger,
    ISessionAudioConnectionManager? sessionAudioConnections = null,
    IConversationPersistence? persistence = null
) : ISessionService
{
    public async Task<Session> SelectResumeAsync(string owner, string id, CancellationToken ct)
    {
        var session = await GetAsync(owner, id, ct);
        Guard.State(session.Status is SessionStatus.Active or SessionStatus.Suspended or SessionStatus.Completed, "session_not_resumable");
        if (persistence is not null && await persistence.IsLeasedAsync(owner, id, ct)) throw new AppException(409, "conversation_in_use");
        if (session.DeviceId is { } deviceId)
        {
            var device = await OwnedDevice(owner, deviceId, ct);
            Guard.State(device.ActiveSessionId is null || device.ActiveSessionId == id, "device_busy");
            device.ActiveSessionId = id;
            await Guard.Save(devices, device, ct);
        }
        session.Status = SessionStatus.Suspended;
        session.EndedAt = null;
        session.AgentSessionId = null;
        await Save(session, ct);
        await Publish(session, ct);
        return session;
    }

    public async Task<Session> ResumeAsync(string owner, string id, CancellationToken ct)
    {
        var session = await GetAsync(owner, id, ct);
        Guard.State(session.Status is SessionStatus.Active or SessionStatus.Suspended or SessionStatus.Starting or SessionStatus.Connecting, "session_not_resumable");
        // A live provider lease belongs to another transport; never steal it.
        if (persistence is not null && await persistence.IsLeasedAsync(owner, id, ct))
            throw new AppException(409, "conversation_in_use");
        if (session.DeviceId is { } deviceId)
        {
            var device = await OwnedDevice(owner, deviceId, ct);
            Guard.State(device.ActiveSessionId is null || device.ActiveSessionId == id, "device_busy");
            if (device.ActiveSessionId is null) { device.ActiveSessionId = id; await Guard.Save(devices, device, ct); }
        }
        var result = await voice.StartSessionAsync(new(id, owner), ct);
        try
        {
            // Provider startup can flush durable transcript events, so reload before the CAS.
            session = await GetAsync(owner, id, ct);
            Guard.State(session.EndedAt is null && session.Status is not SessionStatus.Ending, "session_ended");
            session.AgentSessionId = result.AgentSessionId;
            session.Status = SessionStatus.Active;
            session.DisconnectedAt = null;
            session.FailureCode = null;
            await Save(session, ct);
            await Publish(session, ct);
            return session;
        }
        catch { await voice.EndSessionAsync(result.AgentSessionId, CancellationToken.None); throw; }
    }

    public async Task SuspendAsync(string owner, string id, string? expectedAgentId, CancellationToken ct)
    {
        var session = await GetAsync(owner, id, ct);
        if (session.Status != SessionStatus.Active || session.AgentSessionId != expectedAgentId) return;
        // Keep the provider lease until it has stopped and transcript writes have drained.
        if (expectedAgentId is not null) await voice.EndSessionAsync(expectedAgentId, ct);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            session = await GetAsync(owner, id, ct);
            if (session.Status != SessionStatus.Active || session.AgentSessionId != expectedAgentId) return;
            session.Status = SessionStatus.Suspended;
            session.DisconnectedAt = DateTime.UtcNow;
            session.UpdatedAt = DateTime.UtcNow;
            session.AgentSessionId = null;
            if (await sessions.ReplaceAsync(session, ct)) { await Publish(session, ct); return; }
        }
    }

    public async Task<Session> CreateAsync(string owner, SessionRequest request, CancellationToken ct)
    {
        ValidateRequest(request, requirePrompt: false);
        if (request.DeviceId is not null)
            await OwnedDevice(owner, request.DeviceId, ct);

        var session = new Session
        {
            OwnerAccountId = owner,
            DeviceId = request.DeviceId,
            Title = request.EffectiveName,
            InitialPrompt = request.InitialPrompt,
            PromptFormat = request.PromptFormat
        };
        await sessions.InsertAsync(session, ct);
        return session;
    }

    public async Task<Session> StartNewAsync(string owner, SessionRequest request, string requestId, CancellationToken ct)
    {
        Guard.Input(!string.IsNullOrWhiteSpace(requestId) && requestId.Length <= 200, "invalid_request_id");
        ValidateRequest(request, requirePrompt: true);
        var existing = await sessions.FindAsync(s => s.OwnerAccountId == owner && s.DeviceId == request.DeviceId && s.StartRequestId == requestId, ct);
        if (existing is not null)
            return await ResolveExistingStart(existing, requestId, ct);

        if (request.DeviceId is not null)
        {
            var device = await RecoverDeviceReservation(owner, request.DeviceId, ct);
            Guard.State(device?.ActiveSessionId is null, "device_busy");
        }

        var session = new Session
        {
            OwnerAccountId = owner,
            DeviceId = request.DeviceId,
            Title = request.EffectiveName,
            InitialPrompt = request.InitialPrompt,
            PromptFormat = request.PromptFormat,
            StartRequestId = requestId
        };
        try { await sessions.InsertAsync(session, ct); }
        catch (AppException e) when (e.Code == "duplicate_key")
        {
            existing = await sessions.FindAsync(s => s.OwnerAccountId == owner && s.DeviceId == request.DeviceId && s.StartRequestId == requestId, ct);
            if (existing is null) throw;
            return await ResolveExistingStart(existing, requestId, ct);
        }
        return await StartAsync(owner, session.Id, request.InitialPrompt, request.PromptFormat, requestId, ct);
    }

    public async Task<SessionAudioStartResult> CreateAudioSessionAsync(string owner, string? deviceId, string requestId, string name, int connectionWindowSeconds, CancellationToken ct)
    {
        Guard.Input(!string.IsNullOrWhiteSpace(requestId) && requestId.Length <= 200, "invalid_request_id");
        Guard.Input(!string.IsNullOrWhiteSpace(name) && name.Length <= 200, "invalid_session_name");
        Guard.Input(connectionWindowSeconds is >= 10 and <= 300, "invalid_connection_window");

        var device = deviceId is null ? null : await RecoverDeviceReservation(owner, deviceId, ct);
        if (device is not null)
            Guard.Input(device.OwnerAccountId == owner && device.CredentialHash is not null, "invalid_device_credentials");
        var existing = await sessions.FindAsync(s => s.OwnerAccountId == owner && s.DeviceId == deviceId && s.StartRequestId == requestId, ct);
        if (existing is not null)
        {
            if (existing.Status != SessionStatus.Ready || existing.ConnectionCredentialConsumedAt is not null)
                throw new AppException(409, "session_start_already_consumed");
            return await RefreshAudioSessionAsync(existing, ct);
        }
        if (device is not null) Guard.State(device.ActiveSessionId is null, "device_busy");

        var now = DateTime.UtcNow;
        var session = new Session
        {
            OwnerAccountId = owner,
            DeviceId = deviceId,
            Title = name,
            Status = SessionStatus.Ready,
            StartRequestId = requestId,
            ConnectionCredentialExpiresAt = now.AddSeconds(connectionWindowSeconds),
            UpdatedAt = now
        };
        try { await sessions.InsertAsync(session, ct); }
        catch (AppException e) when (e.Code == "duplicate_key")
        {
            var duplicate = await sessions.FindAsync(s => s.OwnerAccountId == owner && s.DeviceId == deviceId && s.StartRequestId == requestId, ct);
            if (duplicate is null) throw;
            return await RefreshAudioSessionAsync(duplicate, ct);
        }
        if (device is not null)
        {
            try
            {
                device.ActiveSessionId = session.Id;
                await Guard.Save(devices, device, ct);
            }
            catch
            {
                await sessions.DeleteAsync(session, CancellationToken.None);
                throw;
            }
        }
        return new(session, connectionWindowSeconds);
    }

    public async Task<Session> ConnectAccountSessionAsync(string sessionId, string owner, string? deviceId, CancellationToken ct)
    {
        var prior = await sessions.GetAsync(sessionId, ct);
        if (prior?.OwnerAccountId == owner && (deviceId is null || prior.DeviceId == deviceId) &&
            (prior.Status == SessionStatus.Suspended || prior.Status == SessionStatus.Active && persistence is not null &&
                prior.UpdatedAt <= DateTime.UtcNow.AddSeconds(-75) && !await persistence.IsLeasedAsync(owner, sessionId, ct)))
            return await ResumeAsync(owner, sessionId, ct);
        var session = await sessions.ConsumeOwnedConnectionAsync(sessionId, owner, deviceId, DateTime.UtcNow, ct);
        if (session is null) throw new AppException(401, "invalid_session_connection");
        try
        {
            await Publish(session, ct);
            await events.PublishAsync(session.OwnerAccountId, session.Id, "AgentStatusChanged", new { Status = "Connecting" }, ct);
            var result = await voice.StartSessionAsync(new(session.Id, session.OwnerAccountId), ct);
            session.AgentSessionId = result.AgentSessionId;
            session.Status = SessionStatus.Active;
            await Save(session, ct);
            await events.PublishAsync(session.OwnerAccountId, session.Id, "AgentStatusChanged", new { Status = "Connected" }, ct);
            await Publish(session, ct);
            return session;
        }
        catch (Exception e)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (session.AgentSessionId is not null)
            {
                try { await voice.EndSessionAsync(session.AgentSessionId, cleanup.Token); }
                catch (Exception cleanupError) { logger.LogWarning("Session voice cleanup failed {SessionId} {ErrorType}", sessionId, cleanupError.GetType().Name); }
            }
            session.Status = SessionStatus.Failed;
            session.FailureCode = e is AppException app ? app.Code : "provider_connection_failed";
            session.EndedAt = DateTime.UtcNow;
            await sessions.ReplaceAsync(session, cleanup.Token);
            await ReleaseDevice(session, cleanup.Token);
            await Publish(session, cleanup.Token);
            throw;
        }
    }

    private async Task<SessionAudioStartResult> RefreshAudioSessionAsync(Session session, CancellationToken ct)
    {
        Guard.State(session.Status == SessionStatus.Ready && session.ConnectionCredentialConsumedAt is null,
            "session_start_already_consumed");
        if (session.ConnectionCredentialExpiresAt is not { } expires || expires <= DateTime.UtcNow)
        {
            await ExpireReadyAsync(session.OwnerAccountId, session.Id, ct);
            throw new AppException(409, "connection_window_expired");
        }
        if (session.DeviceId is not null)
        {
            var device = Guard.Found(await devices.GetAsync(session.DeviceId, ct));
            Guard.State(device.OwnerAccountId == session.OwnerAccountId && device.CredentialHash is not null, "device_session_unavailable");
            Guard.State(device.ActiveSessionId is null || device.ActiveSessionId == session.Id, "device_busy");
            if (device.ActiveSessionId is null)
            {
                device.ActiveSessionId = session.Id;
                await Guard.Save(devices, device, ct);
            }
        }
        // A retry must not extend the connection deadline or overwrite a concurrent connection claim.
        return new(session, Math.Max(1, (int)Math.Ceiling((expires - DateTime.UtcNow).TotalSeconds)));
    }

    public async Task<Session> GetAsync(string owner, string id, CancellationToken ct) =>
        Guard.Found(await sessions.FindAsync(x => x.Id == id && x.OwnerAccountId == owner, ct));

    public async Task<IReadOnlyList<Session>> ListAsync(string owner, int offset, int limit, CancellationToken ct)
    {
        Guard.Page(offset, limit);
        return await sessions.ListAsync(x => x.OwnerAccountId == owner, offset, limit, ct);
    }

    public async Task<Session> UpdateAsync(string owner, string id, SessionUpdate request, CancellationToken ct)
    {
        var session = await GetAsync(owner, id, ct);
        session.Title = request.Title;
        await Save(session, ct);
        return session;
    }

    public async Task DeleteAsync(string owner, string id, CancellationToken ct)
    {
        var session = await GetAsync(owner, id, ct);
        Guard.State(session.Status == SessionStatus.Created, "only_created_sessions_deletable");
        Guard.State(await sessions.DeleteAsync(session, ct));
    }

    public Task<Session> StartAsync(string owner, string id, CancellationToken ct) =>
        StartAsync(owner, id, null, "text", null, ct);

    public async Task<Session> StartAsync(
        string owner,
        string id,
        string? initialPrompt,
        string promptFormat,
        string? requestId,
        CancellationToken ct
    )
    {
        var session = await GetAsync(owner, id, ct);
        if (requestId is not null)
        {
            Guard.Input(requestId.Length <= 200 && !string.IsNullOrWhiteSpace(requestId), "invalid_request_id");
            if (session.StartRequestId is not null && session.StartRequestId != requestId)
                throw new AppException(409, "idempotency_key_conflict");
            session.StartRequestId ??= requestId;
        }
        if (initialPrompt is not null)
            session.InitialPrompt = initialPrompt;
        session.PromptFormat = promptFormat;

        if (session.Status is SessionStatus.Active or SessionStatus.Completed)
        {
            Guard.State(requestId is not null && session.StartRequestId == requestId, "invalid_session_state");
            return session;
        }
        if (session.Status == SessionStatus.Starting)
            return await WaitForStart(session.Id, session.StartRequestId, ct);

        Guard.State(session.Status == SessionStatus.Created);
        ValidatePrompt(session.InitialPrompt, session.PromptFormat, allowEmpty: true);
        session.Status = SessionStatus.Starting;
        try { await Save(session, ct); }
        catch (AppException e) when (e.Code == "concurrency_conflict" && requestId is not null)
        { return await WaitForStart(session.Id, requestId, ct); }
        try
        {
            if (session.DeviceId is not null)
            {
                var device = await RecoverDeviceReservation(owner, session.DeviceId, ct);
                Guard.State(device.CredentialHash is not null, "device_unpaired");
                Guard.State(device.ActiveSessionId is null || device.ActiveSessionId == session.Id, "device_busy");
                device.ActiveSessionId = session.Id;
                await Guard.Save(devices, device, ct);
            }
            await Publish(session, ct);
            await events.PublishAsync(owner, id, "AgentStatusChanged", new { Status = "Connecting" }, ct);
            var result = await voice.StartSessionAsync(new(session.Id, owner), ct);
            session.AgentSessionId = result.AgentSessionId;
            session.Status = SessionStatus.Active;
            await Save(session, ct);
            if (session.PromptFormat == "text" && !string.IsNullOrWhiteSpace(session.InitialPrompt))
                await voice.SendUserTextAsync(result.AgentSessionId, session.InitialPrompt, ct);
            await events.PublishAsync(owner, id, "AgentStatusChanged", new { Status = "Connected" }, ct);
            await Publish(session, ct);
            return session;
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (session.AgentSessionId is not null)
            {
                try { await voice.EndSessionAsync(session.AgentSessionId, cleanup.Token); }
                catch (Exception e) { logger.LogWarning("Voice cleanup failed {SessionId} {ErrorType}", id, e.GetType().Name); }
            }
            session.Status = SessionStatus.Failed;
            session.FailureCode = "initialization_failed";
            session.EndedAt = DateTime.UtcNow;
            await sessions.ReplaceAsync(session, cleanup.Token);
            await ReleaseDevice(session, cleanup.Token);
            await Publish(session, cleanup.Token);
            throw;
        }
    }

    public async Task<Session> EndAsync(string owner, string id, CancellationToken ct, string? requestId = null)
    {
        var session = await GetAsync(owner, id, ct);
        if (session.Status == SessionStatus.Completed)
            return session;
        if (session.Status == SessionStatus.Ready)
        {
            session.Status = SessionStatus.Completed;
            session.EndedAt = DateTime.UtcNow;
            await Save(session, ct);
            await ReleaseDevice(session, ct);
            await Publish(session, ct);
            await events.PublishAsync(owner, id, "AgentStatusChanged", new { Status = "Disconnected" }, ct);
            return session;
        }
        Guard.State(session.Status is SessionStatus.Active or SessionStatus.Suspended);
        session.Status = SessionStatus.Ending;
        await Save(session, ct);
        await Publish(session, ct);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (session.AgentSessionId is not null)
                await voice.EndSessionAsync(session.AgentSessionId, cleanup.Token);
            session.Status = SessionStatus.Completed;
        }
        catch (Exception e)
        {
            session.Status = SessionStatus.Failed;
            session.FailureCode = "voice_shutdown_failed";
            logger.LogWarning("Session shutdown failed {SessionId} {ErrorType}", id, e.GetType().Name);
        }
        // The provider may flush final transcripts while shutting down. Transcript
        // persistence also updates the meeting document, so reload it before the
        // terminal lifecycle save to avoid a stale-version concurrency conflict.
        var latest = await sessions.GetAsync(id, cleanup.Token);
        if (latest is null || latest.OwnerAccountId != owner)
            throw new AppException(404, "session_not_found");
        if (latest.Status != SessionStatus.Ending)
            return latest;
        latest.Status = session.Status;
        latest.FailureCode = session.FailureCode;
        latest.EndedAt = DateTime.UtcNow;
        await Save(latest, cleanup.Token);
        session = latest;
        await ReleaseDevice(session, cleanup.Token);
        await NotifyTerminal(session, requestId, cleanup.Token);
        await Publish(session, cleanup.Token);
        await events.PublishAsync(owner, id, "AgentStatusChanged", new { Status = "Disconnected" }, cleanup.Token);
        return session;
    }

    public async Task<Session> FailAsync(string owner, string id, string failureCode, CancellationToken ct)
    {
        var session = await GetAsync(owner, id, ct);
        if (session.Status == SessionStatus.Failed) return session;
        Guard.State(session.Status is SessionStatus.Active or SessionStatus.Connecting or SessionStatus.Ready or SessionStatus.Ending, "invalid_session_state");
        session.Status = SessionStatus.Failed;
        session.FailureCode = failureCode;
        session.EndedAt = DateTime.UtcNow;
        await Save(session, ct);
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        if (session.AgentSessionId is not null)
        {
            try { await voice.EndSessionAsync(session.AgentSessionId, cleanup.Token); }
            catch (Exception e) { logger.LogWarning("Failed session provider cleanup {SessionId} {ErrorType}", id, e.GetType().Name); }
        }
        await ReleaseDevice(session, cleanup.Token);
        await NotifyTerminal(session, null, cleanup.Token);
        await Publish(session, cleanup.Token);
        await events.PublishAsync(owner, id, "AgentStatusChanged", new { Status = "Disconnected" }, cleanup.Token);
        return session;
    }

    public async Task<bool> ExpireReadyAsync(string owner, string id, CancellationToken ct)
    {
        var session = await GetAsync(owner, id, ct);
        if (session.Status != SessionStatus.Ready || (session.ConnectionCredentialExpiresAt is { } expires
            ? expires > DateTime.UtcNow : session.UpdatedAt > DateTime.UtcNow.AddMinutes(-2))) return false;
        session.Status = SessionStatus.Failed;
        session.FailureCode = "connection_window_expired";
        session.EndedAt = session.UpdatedAt = DateTime.UtcNow;
        // CAS against the Ready version prevents expiration from terminating a connection that just claimed it.
        if (!await sessions.ReplaceAsync(session, ct)) return false;
        await ReleaseDevice(session, ct);
        await Publish(session, ct);
        return true;
    }

    private async Task<Session> ResolveExistingStart(Session session, string requestId, CancellationToken ct)
    {
        Guard.State(session.StartRequestId == requestId, "idempotency_key_conflict");
        if (session.Status == SessionStatus.Created)
            return await StartAsync(session.OwnerAccountId, session.Id, session.InitialPrompt, session.PromptFormat, requestId, ct);
        if (session.Status == SessionStatus.Starting)
            return await WaitForStart(session.Id, requestId, ct);
        if (session.Status == SessionStatus.Failed)
            throw new AppException(502, session.FailureCode ?? "session_start_failed");
        Guard.State(session.Status is SessionStatus.Active or SessionStatus.Suspended, "session_start_already_consumed");
        return session;
    }

    private async Task<Session> WaitForStart(string id, string? requestId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            var session = Guard.Found(await sessions.GetAsync(id, ct));
            if (session.StartRequestId != requestId)
                throw new AppException(409, "idempotency_key_conflict");
            if (session.Status == SessionStatus.Active || session.Status == SessionStatus.Completed)
                return session;
            if (session.Status == SessionStatus.Suspended)
                return session;
            if (session.Status == SessionStatus.Failed)
                throw new AppException(502, session.FailureCode ?? "session_start_failed");
            if (session.Status != SessionStatus.Starting)
                throw new AppException(409, "session_start_interrupted");
        }
        throw new AppException(503, "session_start_pending");
    }

    private static void ValidateRequest(SessionRequest request, bool requirePrompt)
    {
        Guard.Input(!string.IsNullOrWhiteSpace(request.EffectiveName) && request.EffectiveName.Length <= 200);
        if (requirePrompt && request.PromptFormat == "text")
            Guard.Input(!string.IsNullOrWhiteSpace(request.InitialPrompt), "initial_prompt_required");
        ValidatePrompt(request.InitialPrompt, request.PromptFormat, allowEmpty: !requirePrompt || request.PromptFormat == "audio");
    }

    private static void ValidatePrompt(string? prompt, string format, bool allowEmpty)
    {
        Guard.Input(format is "text" or "audio", "unsupported_prompt_format");
        if (format == "text" && prompt is not null)
            Guard.Input(prompt.Length is > 0 and <= 8192 && !string.IsNullOrWhiteSpace(prompt), "invalid_initial_prompt");
        if (format == "audio")
            Guard.Input(string.IsNullOrEmpty(prompt), "audio_prompt_must_not_include_text");
        if (!allowEmpty && format == "text")
            Guard.Input(!string.IsNullOrWhiteSpace(prompt), "initial_prompt_required");
    }

    private async Task<Device> OwnedDevice(string owner, string id, CancellationToken ct) =>
        Guard.Found(await devices.FindAsync(d => d.Id == id && d.OwnerAccountId == owner, ct));

    private async Task<Device> RecoverDeviceReservation(string owner, string id, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var device = await OwnedDevice(owner, id, ct);
            if (device.ActiveSessionId is null) return device;
            var reserved = await sessions.GetAsync(device.ActiveSessionId, ct);
            if (reserved is { Status: SessionStatus.Ready } && reserved.OwnerAccountId == owner && reserved.DeviceId == id
                && (reserved.ConnectionCredentialExpiresAt is { } expires ? expires <= DateTime.UtcNow : reserved.UpdatedAt <= DateTime.UtcNow.AddMinutes(-2)))
            {
                await ExpireReadyAsync(owner, reserved.Id, ct);
                continue;
            }
            if (reserved is not null && reserved.Status is not (SessionStatus.Completed or SessionStatus.Failed)) return device;
            device.ActiveSessionId = null;
            if (await devices.ReplaceAsync(device, ct)) return device;
        }
        throw new AppException(409, "device_busy");
    }

    private async Task NotifyTerminal(Session session, string? requestId, CancellationToken ct)
    {
        var failed = session.Status == SessionStatus.Failed;
        var control = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, type = failed ? "session.failed" : "session.ended", requestId,
            payload = new { sessionId = session.Id, status = session.Status.ToString(),
                reason = failed ? session.FailureCode : "completed", code = failed ? session.FailureCode : null }
        });
        try
        {
            var sent = sessionAudioConnections is not null && await sessionAudioConnections.SendControlAsync(session.Id, control, ct);
            if (!sent && session.DeviceId is not null) await connections.SendTextAsync(session.DeviceId, control, ct);
        }
        catch (Exception e) { logger.LogDebug("Session terminal notification skipped {SessionId} {ErrorType}", session.Id, e.GetType().Name); }
    }

    private async Task ReleaseDevice(Session session, CancellationToken ct)
    {
        if (session.DeviceId is null)
            return;
        for (var i = 0; i < 5; i++)
        {
            var device = await devices.GetAsync(session.DeviceId, ct);
            if (device is null || device.ActiveSessionId != session.Id)
                return;
            device.ActiveSessionId = null;
            if (await devices.ReplaceAsync(device, ct))
                return;
        }
        logger.LogWarning("Device release deferred {DeviceId}", session.DeviceId);
    }

    private async Task Save(Session session, CancellationToken ct)
    {
        session.UpdatedAt = DateTime.UtcNow;
        await Guard.Save(sessions, session, ct);
    }

    private Task Publish(Session session, CancellationToken ct) =>
        events.PublishAsync(session.OwnerAccountId, session.Id, "SessionStatusChanged", new { session.Id, session.Status }, ct);
}
