using System.Net.WebSockets;
using Quacktion.App.Services;

namespace Quacktion.Web.WebSockets;

public sealed class SessionAudioWebSocketMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISessionAudioWebSocketHandler handler)
    {
        if (!context.Request.Path.StartsWithSegments("/ws/sessions", out var remainder))
        {
            await next(context);
            return;
        }

        var parts = remainder.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts.Length != 2 || parts[1] != "audio") throw new AppException(404, "not_found");
        if (!context.WebSockets.IsWebSocketRequest) throw new AppException(400, "websocket_required");
        if (context.User.Identity?.IsAuthenticated != true)
            throw new AppException(401, "invalid_session_credential");
        var owner = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(owner)) throw new AppException(401, "invalid_session_credential");
        await handler.HandleAsync(context, parts[0], owner);
    }
}
