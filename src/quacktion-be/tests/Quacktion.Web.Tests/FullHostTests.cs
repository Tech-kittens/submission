using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Quacktion.App.DTOs;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Xunit;
namespace Quacktion.Web.Tests;

public sealed class FullHostTests
{
    [MongoFact]
    public async Task RealHostInitializesFailsClosedForVoiceAndExecutesDurableWorkInBackground()
    {
        var uri = Environment.GetEnvironmentVariable("QUACKTION_TEST_MONGO")!;
        var database = "quacktion_test_" + Guid.NewGuid().ToString("N");
        try
        {
            await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SigningKey"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                    ["MongoDB:ConnectionString"] = uri,
                    ["MongoDB:Database"] = database,
                    ["Gemini:ApiKey"] = ""
                }));
                builder.ConfigureServices(s => s.AddSingleton<IAgentGateway, TestTaskGateway>());
            });
            var client = factory.CreateClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while ((await client.GetAsync("/health/ready", timeout.Token)).StatusCode != HttpStatusCode.OK)
                await Task.Delay(100, timeout.Token);
            var registration = await client.PostAsJsonAsync("/api/accounts", new RegisterRequest("host@example.test", "a-secure-password", "Host"), timeout.Token);
            registration.EnsureSuccessStatusCode();
            var account = (await registration.Content.ReadFromJsonAsync<Account>(timeout.Token))!;
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(account.Email, "a-secure-password"), timeout.Token);
            login.EnsureSuccessStatusCode();
            var token = (await login.Content.ReadFromJsonAsync<TokenResult>(timeout.Token))!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            var creation = await client.PostAsJsonAsync("/api/sessions", new SessionRequest("Unavailable voice", null), timeout.Token);
            creation.EnsureSuccessStatusCode();
            var sessionId = (await creation.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("id").GetString();
            var start = await client.PostAsync($"/api/sessions/{sessionId}/start", null, timeout.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, start.StatusCode);
            Assert.Contains("gemini_api_key_not_configured", await start.Content.ReadAsStringAsync(timeout.Token));
            var failed = await client.GetFromJsonAsync<JsonElement>($"/api/sessions/{sessionId}", timeout.Token);
            Assert.Equal("Failed", failed.GetProperty("status").GetString());
            using var scope = factory.Services.CreateScope();
            var finishedMeeting = new Session { OwnerAccountId = account.Id, Title = "Finished meeting", Status = SessionStatus.Completed };
            await scope.ServiceProvider.GetRequiredService<ISessionRepo>().InsertAsync(finishedMeeting, timeout.Token);
            await scope.ServiceProvider.GetRequiredService<IAgentRepo>().InsertAsync(new Agent { Provider = "test", Name = "Test only", Enabled = true, Capabilities = ["internal.summary"] }, timeout.Token);
            var proposed = await client.PostAsJsonAsync($"/api/sessions/{finishedMeeting.Id}/quacktions", new ProposalRequest("internal.summary", "{}"), timeout.Token);
            proposed.EnsureSuccessStatusCode();
            var id = (await proposed.Content.ReadFromJsonAsync<JsonElement>(timeout.Token)).GetProperty("id").GetString();
            JsonElement action;
            do
            {
                await Task.Delay(100, timeout.Token);
                action = await client.GetFromJsonAsync<JsonElement>($"/api/quacktions/{id}", timeout.Token);
            } while (action.GetProperty("status").GetString() is "Queued" or "InProgress");
            Assert.Equal("Completed", action.GetProperty("status").GetString());
            Assert.Equal(1, action.GetProperty("attempts").GetInt32());
            Assert.Single(action.GetProperty("history").EnumerateArray());
        }
        finally { await new MongoClient(uri).DropDatabaseAsync(database); }
    }
    private sealed class TestTaskGateway : IAgentGateway
    {
        public string Provider => "test";
        public bool Supports(string capability) => capability == "internal.summary";
        public Task<AgentTaskResult> ExecuteAsync(AgentTaskRequest request, CancellationToken ct)
        {
            Assert.NotEmpty(request.OwnerAccountId);
            Assert.NotEmpty(request.SessionId);
            Assert.NotEmpty(request.IdempotencyKey);
            return Task.FromResult(new AgentTaskResult(true, "{\"summary\":\"Test result\"}", null));
        }
    }
}
