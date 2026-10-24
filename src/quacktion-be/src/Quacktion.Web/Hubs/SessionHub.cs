using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Quacktion.App.Interfaces;
using Quacktion.App.Services;
namespace Quacktion.Web.Hubs;

[Authorize]
public sealed class SessionHub(ISessionService sessions, IAccountService accounts) : Hub
{
    public static string Group(string owner, string session) => $"account:{owner}:session:{session}";
    public async Task JoinSession(string sessionId)
    {
        var owner = Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!;
        try { await accounts.GetAsync(owner, Context.ConnectionAborted); await sessions.GetAsync(owner, sessionId, Context.ConnectionAborted); }
        catch (AppException) { throw new HubException("not_found"); }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(owner, sessionId));
    }
    public Task LeaveSession(string sessionId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, Group(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!, sessionId));
}
public sealed class SessionEvents(IHubContext<SessionHub> hub, IAccountRepo accounts, ILogger<SessionEvents> logger) : ISessionEvents
{
    public async Task PublishAsync(string owner, string session, string eventName, object payload, CancellationToken ct)
    {
        try
        {
            if (await accounts.GetAsync(owner, ct) is not { IsActive: true }) return;
            await hub.Clients.Group(SessionHub.Group(owner, session)).SendAsync(eventName, payload, ct);
        }
        catch (Exception e) { logger.LogWarning("Notification failed {SessionId} {EventName} {ErrorType}", session, eventName, e.GetType().Name); }
    }
}
