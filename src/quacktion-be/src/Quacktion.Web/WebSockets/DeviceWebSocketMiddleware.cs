using Quacktion.App.Interfaces;
using Quacktion.App.Services;
namespace Quacktion.Web.WebSockets;

public sealed class DeviceWebSocketMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IDeviceAuthenticationService authentication, IDeviceWebSocketHandler handler)
    {
        if (!context.Request.Path.StartsWithSegments("/ws/devices", out var remainder)) { await next(context); return; }
        var id = remainder.Value?.Trim('/');
        if (string.IsNullOrEmpty(id) || id.Contains('/')) throw new AppException(404, "not_found");
        var header = context.Request.Headers.Authorization.ToString();
        var credential = header.StartsWith("Device ", StringComparison.Ordinal) ? header[7..] : "";
        var device = await authentication.AuthenticateAsync(id, credential, context.RequestAborted);
        if (device is null) throw new AppException(401, "invalid_device_credentials");
        if (!context.WebSockets.IsWebSocketRequest) throw new AppException(400, "websocket_required");
        if (!context.WebSockets.WebSocketRequestedProtocols.Contains("quacktion.v1")) throw new AppException(400, "unsupported_protocol_version");
        await handler.HandleAsync(context, device, credential);
    }
}
