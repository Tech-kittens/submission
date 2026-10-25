using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Quacktion.Web.Controllers;

public sealed class AndroidAppLinksSettings
{
    public string PackageName { get; set; } = "com.example.quacktion_frontend";
    public string[] Sha256CertFingerprints { get; set; } = [];
}

[AllowAnonymous]
public sealed class MobileMcpController(IOptions<AndroidAppLinksSettings> settings) : ControllerBase
{
    [HttpGet("/mobile/mcp-complete")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ContentResult Complete() => Content("""
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="referrer" content="no-referrer"><title>Return to Quacktion</title>
        <style>body{max-width:36rem;margin:15vh auto;padding:1.5rem;font:18px/1.6 system-ui;color:#18332e;background:#f3f6f3}</style>
        </head><body><h1>Account authorization complete</h1>
        <p>Return to Quacktion to view your connections.</p></body></html>
        """, "text/html; charset=utf-8");

    [HttpGet("/.well-known/assetlinks.json")]
    public IActionResult AssetLinks() => new JsonResult(settings.Value.Sha256CertFingerprints.Length == 0
        ? Array.Empty<object>()
        : new object[] { new {
            relation = new[] { "delegate_permission/common.handle_all_urls" },
            target = new {
                @namespace = "android_app", package_name = settings.Value.PackageName,
                sha256_cert_fingerprints = settings.Value.Sha256CertFingerprints
            }
        } });
}
