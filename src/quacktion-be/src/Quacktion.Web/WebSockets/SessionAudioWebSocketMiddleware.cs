using System.Net.WebSockets;
using Quacktion.App.Interfaces;
using Quacktion.App.Services;

namespace Quacktion.Web.WebSockets;

public sealed class SessionAudioWebSocketMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ISessionAudioWebSocketHandler handler, IDeviceAuthenticationService deviceAuthentication)
    {
        if (!context.Request.Path.StartsWithSegments("/ws/sessions", out var remainder))
        {
            await next(context);
            return;
        }

        var parts = remainder.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (parts.Length != 2 || parts[1] != "audio") throw new AppException(404, "not_found");
        if (!context.WebSockets.IsWebSocketRequest) throw new AppException(400, "websocket_required");
        var authorization = context.Request.Headers.Authorization.ToString();
        string? owner = null;
        string? deviceId = null;
        if (authorization.StartsWith("Device ", StringComparison.OrdinalIgnoreCase))
        {
            var device = await deviceAuthentication.AuthenticateCredentialAsync(authorization[7..], context.RequestAborted);
            if (device is null) throw new AppException(401, "invalid_device_credentials");
            owner = device.OwnerAccountId;
            deviceId = device.Id;
        }
        else if (context.User.Identity?.IsAuthenticated == true)
            owner = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(owner)) throw new AppException(401, "invalid_session_authentication");
        await handler.HandleAsync(context, parts[0], owner, deviceId);
    }
}
