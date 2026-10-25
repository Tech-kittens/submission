using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Quacktion.App.Services;
using Quacktion.Web.Controllers;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace Quacktion.Web.Tests;

public sealed class McpCallbackTests
{
    [Fact]
    public async Task SavedConnectionRedirectsToFixedDestinationAndReplayFails()
    {
        var f = await McpConnectionTests.Harness.Create();
        var uri = await f.Service.BeginConnectAsync(f.Owner, "google", default);
        var state = QueryHelpers.ParseQuery(uri.Query)["state"].ToString();
        var controller = new McpController(f.Service) {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.QueryString = new QueryString("?returnUrl=https://attacker.test&code=private&state=private");
        var result = Assert.IsType<RedirectResult>(await controller.Callback("test-code", state, null, default));
        Assert.Equal("https://quackback.ofneill.com/mobile/mcp-complete", result.Url);
        Assert.False(result.Permanent);
        Assert.Single(await f.Service.ListAsync(f.Owner, default));
        await Assert.ThrowsAsync<AppException>(() => controller.Callback("test-code", state, null, default));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("code", null, null)]
    [InlineData("code", "state", "denied-private-description")]
    public async Task InvalidOrDeniedCallbackCannotReachSuccess(string? code, string? state, string? error)
    {
        var f = await McpConnectionTests.Harness.Create();
        var result = await new McpController(f.Service).Callback(code, state, error, default);
        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Empty(await f.Service.ListAsync(f.Owner, default));
    }

    [Fact]
    public async Task UnknownStateCannotRedirect()
    {
        var f = await McpConnectionTests.Harness.Create();
        await Assert.ThrowsAsync<AppException>(() => new McpController(f.Service).Callback("code", "unknown", null, default));
        Assert.Empty(await f.Service.ListAsync(f.Owner, default));
    }

    [Fact]
    public async Task ExpiredStateCannotExchangeOrRedirect()
    {
        var f = await McpConnectionTests.Harness.Create();
        var uri = await f.Service.BeginConnectAsync(f.Owner, "google", default);
        var state = QueryHelpers.ParseQuery(uri.Query)["state"].ToString();
        var tx = (await f.Transactions.GetAsync(Secrets.Hash(state), default))!;
        tx.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await f.Transactions.ReplaceAsync(tx, default);
        await Assert.ThrowsAsync<AppException>(() => new McpController(f.Service).Callback("code", state, null, default));
        Assert.Equal(0, f.Http.TokenCalls);
        Assert.Empty(await f.Service.ListAsync(f.Owner, default));
    }

    [Fact]
    public async Task InvalidTokenExchangeCannotPersistOrRedirect()
    {
        var f = await McpConnectionTests.Harness.Create();
        f.Http.ExpiresIn = 0;
        var uri = await f.Service.BeginConnectAsync(f.Owner, "google", default);
        var state = QueryHelpers.ParseQuery(uri.Query)["state"].ToString();
        var error = await Assert.ThrowsAsync<AppException>(() => new McpController(f.Service).Callback("private-code", state, null, default));
        Assert.Equal("mcp_token_expiry_invalid", error.Code);
        Assert.Empty(await f.Service.ListAsync(f.Owner, default));
    }

    [Fact]
    public void FallbackAndAssociationContainOnlyPublicStaticInformation()
    {
        var controller = new MobileMcpController(Options.Create(new AndroidAppLinksSettings()));
        Assert.Contains("Return to Quacktion", controller.Complete().Content);
        Assert.Equal("text/html; charset=utf-8", controller.Complete().ContentType);
        Assert.Empty(Assert.IsType<object[]>(Assert.IsType<JsonResult>(controller.AssetLinks()).Value));
        var settings = new AndroidAppLinksSettings { Sha256CertFingerprints = [string.Join(":", Enumerable.Repeat("AB", 32))] };
        var json = System.Text.Json.JsonSerializer.Serialize(Assert.IsType<JsonResult>(new MobileMcpController(Options.Create(settings)).AssetLinks()).Value);
        Assert.Contains("sha256_cert_fingerprints", json);
        Assert.Contains("android_app", json);
        Assert.DoesNotContain("REPLACE", json);
    }

    [Fact]
    public async Task HostedAssociationIncludesExistingAndLocalTestCertificatesWithoutRedirect()
    {
        await using var factory = new ApiFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/.well-known/assetlinks.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(response.Headers.Location);
        var association = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        var statement = Assert.Single(association.EnumerateArray());
        var target = statement.GetProperty("target");
        Assert.Equal("com.example.quacktion_frontend", target.GetProperty("package_name").GetString());
        var fingerprints = target.GetProperty("sha256_cert_fingerprints").EnumerateArray()
            .Select(x => x.GetString()).ToArray();
        Assert.Contains("1A:8A:2C:E4:85:15:F2:BD:15:A8:24:CD:10:89:B9:A3:B8:A7:A7:3A:61:46:05:F2:9D:65:F7:82:6F:2C:E3:F5", fingerprints);
        Assert.Contains("EB:34:7A:2F:8C:50:1A:1B:57:B3:54:57:4A:9A:1A:C4:48:47:BD:D3:0D:29:C2:53:F2:A1:B3:75:CE:19:B7:06", fingerprints);
        Assert.Contains("delegate_permission/common.handle_all_urls",
            statement.GetProperty("relation").EnumerateArray().Select(x => x.GetString()));
    }
}
