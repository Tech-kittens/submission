using System.Security.Claims;
using Quacktion.App.Interfaces;
using Quacktion.App.Services;

namespace Quacktion.Web.Infrastructure;

public sealed class MeetingDeviceAuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IDeviceAuthenticationService authentication)
    {
        var path = context.Request.Path;
        var isMeetingRoute = path.StartsWithSegments("/api/meetings");
        var parts = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        var isDeviceMeetingRoute = parts.Length == 4 && parts[0] == "api" && parts[1] == "devices"
            && parts[3] is "meetings" or "mode";
        var authorization = context.Request.Headers.Authorization.ToString();

        if ((isMeetingRoute || isDeviceMeetingRoute)
            && authorization.StartsWith("Device ", StringComparison.OrdinalIgnoreCase))
        {
            var device = await authentication.AuthenticateCredentialAsync(authorization[7..], context.RequestAborted);
            if (device is null || (isDeviceMeetingRoute && device.Id != parts[2]))
                throw new AppException(401, "invalid_device_credentials");

            context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, device.OwnerAccountId),
                new Claim("device_id", device.Id)
            ], "Device"));
        }

        await next(context);
    }
}
