using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Tests;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Xunit;

namespace Quacktion.Web.Tests;

public sealed class AgentMcpTests
{
    [Fact]
    public async Task JevReceivesOnlyTheQueryAndAvailableProviders()
    {
        var handler = new JevHandler("""
            {"answers":{"provider":{"type":"choice","choice":"argon4","confidence":0.96,"probabilities":{"argon4":0.96,"gemini":0.04}}}}
            """);
        var router = new JevAgentRouter(new TestHttpClientFactory(handler),
            Options.Create(new JevSettings { Enabled = true, ApiKey = "test-only", MinimumConfidence = 0.75 }), NullLogger<JevAgentRouter>.Instance);

        var decision = await router.RouteAsync(new("internal.analysis", "{\"query\":\"check calendar\",\"credential\":\"private-token\"}",
        [new("argon4", ["internal.analysis"]), new("gemini", ["internal.analysis"])]), default);

        Assert.Equal("argon4", decision!.Provider);
        Assert.Equal(0.96, decision.Confidence);
        Assert.Contains("check calendar", handler.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-token", handler.Body, StringComparison.Ordinal);
        Assert.Equal("Bearer test-only", handler.Authorization);
    }

    [Fact]
    public async Task JevIgnoresProviderOutsideCandidateAllowlist()
    {
        var handler = new JevHandler("""
            {"answers":{"provider":{"type":"choice","choice":"attacker","confidence":1.0}}}
            """);
        var router = new JevAgentRouter(new TestHttpClientFactory(handler),
            Options.Create(new JevSettings { Enabled = true, ApiKey = "test-only" }), NullLogger<JevAgentRouter>.Instance);

        var decision = await router.RouteAsync(new("internal.analysis", "{}",
            [new("argon4", ["internal.analysis"]), new("gemini", ["internal.analysis"])]), default);

        Assert.Null(decision);
    }

    [Fact]
    public async Task JevSkillSuggestionMustBeAnAvailableRegistrySkill()
    {
        var handler = new JevHandler("""{"answers":{"skill":{"choice":"gmail.delete_message","confidence":1.0}}}""");
        var router = new JevAgentRouter(new TestHttpClientFactory(handler),
            Options.Create(new JevSettings { Enabled = true, ApiKey = "test-only" }), NullLogger<JevAgentRouter>.Instance);
        var available = new[] { new McpSkillDefinition { Id = "gmail.get_unread", Available = true, Description = "Read unread mail" },
            new McpSkillDefinition { Id = "gmail.delete_message", Available = false, Description = "Unavailable" } };
        Assert.Null(await router.SuggestSkillAsync("Show unread mail", available, default));
        Assert.Contains("gmail.get_unread", handler.Body);
        Assert.DoesNotContain("gmail.delete_message", handler.Body);
    }

    [Theory]
    [InlineData("http://mcp.adriantd.com/")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("https://mcp.adriantd.com@attacker.test/private")]
    public void McpRegistryRejectsUnsafeBaseUrls(string url)
    {
        var registry = new McpServerRegistry(Options.Create(new McpSettings { BaseUrl = url }));
        Assert.Throws<AppException>(() => registry.All());
    }

    [Fact]
    public async Task JevRoutesAmongAuthorizedReasonersAndFallbackPreservesTaskRouting()
    {
        var argon = new RecordingGateway("argon4");
        var flash = new RecordingGateway("gemini");
        var router = new FixedRouter(new AgentRouteDecision("argon4", "complex", 0.95));
        var orchestrator = new AgentOrchestrator([argon, flash], new Agents(), router);
        var request = new AgentTaskRequest("internal.analysis", "{}", "task-key", "account", "session");

        var routed = await orchestrator.ExecuteAsync(request, default);
        Assert.True(routed.Success);
        Assert.Equal(1, argon.Calls);
        Assert.Equal(0, flash.Calls);

        var fallback = new AgentOrchestrator([argon, flash], new Agents(), new FixedRouter(null));
        await fallback.ExecuteAsync(request with { ActionType = "internal.summary" }, default);
        Assert.Equal(1, flash.Calls);
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false) { BaseAddress = new Uri("https://jev-ai.org/") };
    }

    private sealed class JevHandler(string response) : HttpMessageHandler
    {
        public string Body { get; private set; } = "";
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            Authorization = request.Headers.Authorization?.ToString();
            return new(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
    }

    private sealed class RecordingGateway(string provider) : IAgentGateway
    {
        public int Calls { get; private set; }
        public string Provider => provider;
        public bool Supports(string capability) => MeetingToolService.Capabilities.Contains(capability);
        public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        { Calls++; return Task.FromResult(new AgentTaskResult(true, "{}", null)); }
    }

    private sealed class FixedRouter(AgentRouteDecision? decision) : IAgentRouter
    {
        public Task<AgentRouteDecision?> RouteAsync(AgentRouteRequest request, CancellationToken ct) => Task.FromResult(decision);
    }
}
