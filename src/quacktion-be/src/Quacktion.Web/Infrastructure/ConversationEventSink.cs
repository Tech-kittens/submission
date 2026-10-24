using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;

namespace Quacktion.Web.Infrastructure;

public sealed class ConversationEventSink(
    IServiceScopeFactory scopes,
    IDeviceConnectionManager devices,
    ILogger<ConversationEventSink> logger
) : BackgroundService, IConversationEventSink
{
    private sealed record QueuedEvent(string SessionId, string ConversationId, ElevenLabsEvent Event);
    private readonly Channel<QueuedEvent> queue = Channel.CreateBounded<QueuedEvent>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false }
    );

    public Task HandleAsync(string sessionId, string conversationId, ElevenLabsEvent item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (item.Audio is { Length: > 0 }) return Task.CompletedTask;

        if (!queue.Writer.TryWrite(new(sessionId, conversationId, item)))
            logger.LogWarning("Conversation event queue full {SessionId} {EventType}", sessionId, item.Type);
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var queued in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                if (queued.Event.Type is "user_transcript" or "agent_response")
                    await SaveTranscriptAsync(queued, stoppingToken);
                else if (queued.Event.Type is "disconnected" or "error")
                    await FailSessionAsync(queued.SessionId, queued.Event.ErrorCode ?? "provider_disconnected", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception e)
            {
                logger.LogWarning("Conversation event processing failed {SessionId} {EventType} {ErrorType}", queued.SessionId, queued.Event.Type, e.GetType().Name);
            }
        }
    }

    private async Task SaveTranscriptAsync(QueuedEvent queued, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queued.Event.Text))
            return;
        using var scope = scopes.CreateScope();
        var transcripts = scope.ServiceProvider.GetRequiredService<ITranscriptService>();
        var speaker = queued.Event.Type == "user_transcript" ? "user" : "agent";
        var entry = new TranscriptEntry
        {
            SessionId = queued.SessionId,
            SpeakerIdentifier = speaker,
            Text = queued.Event.Text,
            Source = "elevenlabs",
            SourceEventId = $"{queued.Event.Type}:{queued.ConversationId}:{queued.Event.EventId ?? Guid.NewGuid().ToString("N")}"
        };
        await transcripts.AppendAsync(entry, ct);
    }

    private async Task FailSessionAsync(string id, string failureCode, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sessionRepo = scope.ServiceProvider.GetRequiredService<ISessionRepo>();
        var session = await sessionRepo.GetAsync(id, ct);
        if (session is null || session.Status is not (SessionStatus.Active or SessionStatus.Starting))
            return;
        session.Status = SessionStatus.Failed;
        session.FailureCode = failureCode;
        session.EndedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        if (!await sessionRepo.ReplaceAsync(session, ct))
            return;

        if (session.DeviceId is not null)
        {
            var devicesRepo = scope.ServiceProvider.GetRequiredService<IDeviceRepo>();
            for (var i = 0; i < 5; i++)
            {
                var device = await devicesRepo.GetAsync(session.DeviceId, ct);
                if (device is null || device.ActiveSessionId != session.Id)
                    break;
                device.ActiveSessionId = null;
                if (await devicesRepo.ReplaceAsync(device, ct))
                    break;
            }
            var control = JsonSerializer.SerializeToUtf8Bytes(new { version = 1, type = "session.ended", payload = new { sessionId = session.Id, status = "Failed", code = failureCode } });
            await devices.SendTextAsync(session.DeviceId, control, ct);
        }
        var events = scope.ServiceProvider.GetRequiredService<ISessionEvents>();
        await events.PublishAsync(session.OwnerAccountId, session.Id, "SessionStatusChanged", new { session.Id, session.Status }, ct);
        await events.PublishAsync(session.OwnerAccountId, session.Id, "AgentStatusChanged", new { Status = "Disconnected" }, ct);
        logger.LogWarning("Conversation ended unexpectedly {SessionId} {FailureCode}", session.Id, failureCode);
    }
}
