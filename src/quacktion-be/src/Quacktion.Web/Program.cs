using Quacktion.Data.Preparation;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Quacktion.App.Interfaces;
using Quacktion.App.Models;
using Quacktion.App.Services;
using Quacktion.Data.Agents;
using Quacktion.Data.Audio;
using Quacktion.Data.MongoDB;
using Quacktion.Data.Repositories;
using Quacktion.Data.Security;
using Quacktion.Web.Controllers;
using Quacktion.Web.Hubs;
using Quacktion.Web.Infrastructure;
using Quacktion.Web.WebSockets;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateScopes = true;
    o.ValidateOnBuild = true;
});
// Hosting diagnostics include query strings in request-start messages. Suppress that
// category so OAuth code/state never enter application access logs (including failures).
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
var services = builder.Services;
services.AddOptions<AndroidAppLinksSettings>()
    .BindConfiguration("AndroidAppLinks")
    .Validate(s => !string.IsNullOrWhiteSpace(s.PackageName)
        && s.Sha256CertFingerprints.All(f => f is not null && System.Text.RegularExpressions.Regex.IsMatch(
            f, @"\A(?:[0-9A-Fa-f]{2}:){31}[0-9A-Fa-f]{2}\z")),
        "Android App Links require a package name and actual colon-separated SHA-256 certificate fingerprints.")
    .ValidateOnStart();
services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddEndpointsApiExplorer();
services.AddSwaggerGen(options =>
{
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Account access token. Swagger sends Authorization: Bearer <token>. A device ID is not authentication."
    });
    options.DocumentFilter<BearerSecurityDocumentFilter>();
});
services.AddProblemDetails();
services.AddExceptionHandler<ExceptionHandler>();
services.Configure<MongoDbSettings>(builder.Configuration.GetSection("MongoDB"));
services.Configure<AudioSettings>(builder.Configuration.GetSection("Audio"));
services.AddOptions<GeminiSettings>().Bind(builder.Configuration.GetSection("Gemini"))
    .Validate(s => builder.Environment.IsDevelopment() || !string.IsNullOrWhiteSpace(s.ApiKey), "Gemini:ApiKey is required in production.")
    .Validate(s => System.Text.RegularExpressions.Regex.IsMatch(s.LiveModel, @"^(models/)?[a-zA-Z0-9._-]+$")
        && System.Text.RegularExpressions.Regex.IsMatch(s.ReasoningModel, @"^[a-zA-Z0-9._-]+$"), "Gemini model names are invalid.")
    .Validate(s => !string.IsNullOrWhiteSpace(s.Voice) && !string.IsNullOrWhiteSpace(s.SystemInstruction)
        && s.ConnectionTimeoutSeconds is >= 1 and <= 60 && s.ReconnectAttempts is >= 1 and <= 10, "Gemini settings are invalid.")
    .Validate(s => s.SilenceDurationMs is >= 200 and <= 2000 && s.PrefixPaddingMs is >= 0 and <= 1000
        && s.StartOfSpeechSensitivity is "START_SENSITIVITY_LOW" or "START_SENSITIVITY_HIGH"
        && s.EndOfSpeechSensitivity is "END_SENSITIVITY_LOW" or "END_SENSITIVITY_HIGH", "Gemini activity detection settings are invalid.")
    .ValidateOnStart();
services.AddOptions<JevSettings>().Bind(builder.Configuration.GetSection("Agents:Router"))
    .Validate(s => System.Text.RegularExpressions.Regex.IsMatch(s.Model, @"^[a-zA-Z0-9._-]+$")
        && s.MinimumConfidence is >= 0 and <= 1 && s.TimeoutSeconds is >= 1 and <= 10, "Jev router settings are invalid.")
    .ValidateOnStart();
services.AddOptions<McpSettings>().Bind(builder.Configuration.GetSection("Mcp"))
    .Validate(s =>
    {
        try { _ = new McpServerRegistry(Microsoft.Extensions.Options.Options.Create(s)).All(); }
        catch { return false; }
        return true;
    }, "Mcp:BaseUrl or Mcp:Providers service paths are invalid. Use an HTTPS origin with no path for BaseUrl and relative service paths without a leading slash.")
    .Validate(s => s.ConnectionTimeoutSeconds is >= 1 and <= 120,
        "Mcp:ConnectionTimeoutSeconds must be between 1 and 120.")
    .Validate(s => s.ToolTimeoutSeconds is >= 1 and <= 120,
        "Mcp:ToolTimeoutSeconds must be between 1 and 120.")
    .Validate(s => !s.Enabled || IsValidMcpEncryptionKey(s.CredentialEncryptionKey),
        "Mcp:CredentialEncryptionKey must be a Base64-encoded 32-byte key when MCP is enabled. Generate once with: openssl rand -base64 32")
    .Validate(s => !s.Enabled || Uri.TryCreate(s.OAuthCallbackUrl, UriKind.Absolute, out var callback) && callback.Scheme == "https",
        "Mcp:OAuthCallbackUrl must be an absolute HTTPS URL when MCP is enabled.")
    .ValidateOnStart();
services.Configure<SessionAudioSettings>(builder.Configuration.GetSection("SessionAudio"));
services.Configure<SessionLifecycleSettings>(builder.Configuration.GetSection("Sessions"));
services.AddOptions<MeetingAnalysisSettings>().Bind(builder.Configuration.GetSection("MeetingAnalysis"))
    .Validate(s => s.WindowIntervalSeconds is >= 30 and <= 3600 && s.RetentionDays is >= 0 and <= 3650,
        "Meeting analysis interval or retention days are invalid.")
    .ValidateOnStart();
services
    .AddOptions<JwtSettings>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .Validate(
        s => Encoding.UTF8.GetByteCount(s.SigningKey) >= 32,
        "Jwt:SigningKey must contain at least 32 bytes; configure a random secret."
    )
    .ValidateOnStart();
services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
services
    .AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<Microsoft.Extensions.Options.IOptions<JwtSettings>>(
        (o, settings) =>
        {
            var jwt = settings.Value;
            o.TokenValidationParameters = new()
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ClockSkew = TimeSpan.FromSeconds(15),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            };
            o.Events = new JwtBearerEvents
            {
                OnMessageReceived = context =>
                {
                    if (
                        (context.Request.Path.StartsWithSegments("/hubs/sessions")
                            || context.Request.Path.StartsWithSegments("/ws/sessions"))
                        && context.Request.Query.TryGetValue("access_token", out var token)
                    )
                        context.Token = token;
                    return Task.CompletedTask;
                },
                OnTokenValidated = async context =>
                {
                    var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    var account = id is null
                        ? null
                        : await context
                            .HttpContext.RequestServices.GetRequiredService<IAccountRepo>()
                            .GetAsync(id, context.HttpContext.RequestAborted);
                    if (
                        account is not { IsActive: true }
                        || context.Principal?.FindFirstValue("stamp") != account.SecurityStamp
                    )
                        context.Fail("Credentials revoked");
                },
            };
        }
    );
services.AddAuthorization();
services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    // Railway's public edge overwrites X-Forwarded-Proto. Its proxy addresses are dynamic.
    // Only the scheme is trusted here; do not use forwarded host or client-IP values.
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
services.AddCors(o =>
    o.AddDefaultPolicy(p =>
    {
        if (!builder.Environment.IsDevelopment())
            p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        else if (origins.Length > 0)
            p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        else
            p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    })
);
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(c =>
        RateLimitPartition.GetFixedWindowLimiter(
            c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ =>
                new()
                {
                    PermitLimit = 300,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                }
        )
    );
    o.AddPolicy(
        "sensitive",
        c =>
            RateLimitPartition.GetFixedWindowLimiter(
                c.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ =>
                    new()
                    {
                        PermitLimit = 10,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0,
                    }
            )
    );
    o.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        await context.HttpContext.Response.WriteAsJsonAsync(
            new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = 429,
                Title = "rate_limit_exceeded",
            },
            ct
        );
    };
});
services
    .AddSignalR(o =>
    {
        o.MaximumReceiveMessageSize = 16384;
        o.EnableDetailedErrors = false;
    })
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddSingleton<IMongoDbContext, MongoDbContext>();
services.AddSingleton<DatabaseReadiness>();
services.AddScoped<IAccountRepo, AccountRepo>();
services.AddScoped<IRefreshTokenRepo, RefreshTokenRepo>();
services.AddScoped<IDeviceRepo, DeviceRepo>();
services.AddScoped<IPairingChallengeRepo, PairingChallengeRepo>();
services.AddScoped<ISessionRepo, SessionRepo>();
services.AddScoped<IMeetingRepo, MeetingRepo>();
services.AddScoped<IMeetingAnalysisRepo, MeetingAnalysisRepo>();
services.AddScoped<IMeetingInsightRepo, MeetingInsightRepo>();
services.AddScoped<IMeetingTranscriptTurnRepo, MeetingTranscriptTurnRepo>();
services.AddScoped<IMeetingSegmentRepo, MeetingSegmentRepo>();
services.AddScoped<IMeetingEventRepo, MeetingEventRepo>();
services.AddScoped<IMeetingTranscriber, GeminiMeetingTranscriber>();
services.Configure<MeetingAudioSettings>(builder.Configuration.GetSection("MeetingAudio"));
services.AddSingleton<IMeetingAudioStore, MeetingAudioStore>();
services.AddScoped<IFinalMeetingTranscriber, Gemini35MeetingTranscriber>();
services.AddScoped<GeminiMeetingIntelligence>();
services.AddScoped<MeetingService>();
services.AddScoped<MeetingDeletionService>();
services.AddScoped<DeviceMeetingStream>();
services.AddScoped<ITranscriptRepo, TranscriptRepo>();
services.AddScoped<IQuacktionRepo, QuacktionRepo>();
services.AddScoped<IUserMcpConnectionRepo, UserMcpConnectionRepo>();
services.AddScoped<IMcpServerRegistrationRepo, McpServerRegistrationRepo>();
services.AddScoped<IMcpCredentialRepo, McpCredentialRepo>();
services.AddScoped<IMcpOAuthTransactionRepo, McpOAuthTransactionRepo>();
services.AddScoped<IMcpExecutionAuditRepo, McpExecutionAuditRepo>();
services.AddScoped<IMcpSkillExecutionRepo, McpSkillExecutionRepo>();
services.AddScoped<IAgentRepo, AgentRepo>();
services.AddScoped<IAccountService, AccountService>();
services.AddScoped<IPasswordHasher<Account>, PasswordHasher<Account>>();
services.AddScoped<ITokenIssuer, JwtTokenIssuer>();
services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210000);
services.AddScoped<IDeviceService, DeviceService>();
services.AddScoped<IDeviceAuthenticationService, DeviceAuthenticationService>();
services.AddScoped<MobileMeetingAudioService>();
services.AddScoped<ISessionService, SessionService>();
services.AddScoped<IQuacktionService, QuacktionService>();
services.AddScoped<IApprovalService, ApprovalService>();
services.AddScoped<IQuacktionExecutionService, QuacktionExecutionService>();
services.AddSingleton<IActionRiskEvaluator, ActionRiskEvaluator>();
services.AddScoped<ITranscriptService, TranscriptService>();
services.AddScoped<IResearchService, ResearchService>();
services.AddScoped<IClaimVerificationService, ResearchService>();
services.AddScoped<IAgentCatalog, AgentCatalog>();
services.AddScoped<IAgentOrchestrator, AgentOrchestrator>();
services.AddSingleton<IAgentGateway, QuacktionAgentGateway>();
services.AddHttpClient("Gemini", c =>
{
    c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    c.Timeout = TimeSpan.FromSeconds(90);
});
services.AddHttpClient("GeminiMeeting", c =>
{
    c.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    c.Timeout = TimeSpan.FromMinutes(5);
});
services.AddHttpClient("Jev", c =>
{
    c.BaseAddress = new Uri("https://jev-ai.org/");
    c.Timeout = TimeSpan.FromSeconds(12);
});
services.AddScoped<IAgentRouter, JevAgentRouter>();
services.AddScoped<IJevSkillRouter>(sp => (JevAgentRouter)sp.GetRequiredService<IAgentRouter>());
services.AddSingleton<IMcpServerRegistry, McpServerRegistry>();
services.AddScoped<IMcpClientGateway, McpClientGateway>();
services.AddHttpClient("Mcp", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(McpHttpSecurity.CreateHandler);
services.AddHttpClient("McpOAuth", c => c.Timeout = TimeSpan.FromSeconds(20))
    .ConfigurePrimaryHttpMessageHandler(McpHttpSecurity.CreateHandler);
services.AddScoped<McpOAuthClient>();
services.AddScoped<IAgentGateway, McpOperationGateway>();
services.AddScoped<McpConnectionService>();
services.AddScoped<IMcpConnectionService>(sp => sp.GetRequiredService<McpConnectionService>());
services.AddScoped<IMcpToolGateway>(sp => sp.GetRequiredService<McpConnectionService>());
services.AddScoped<IMcpSkillRegistry, McpSkillRegistry>();
services.AddScoped<IMcpSkillExecutor, McpSkillExecutor>();
services.AddScoped<IDeviceTransportPersistence, Quacktion.Data.Sessions.DeviceTransportPersistence>();
services.AddSingleton<Quacktion.Data.Sessions.HandleCipher>();
services.AddScoped<IConversationPersistence, Quacktion.Data.Sessions.ConversationPersistence>();
services.AddScoped<IConversationContextService, Quacktion.Data.Sessions.ConversationContextService>();
services.AddScoped<IRealtimeStateRepo, RealtimeStateRepo>();
services.AddScoped<IMeetingToolCallRepo, MeetingToolCallRepo>();
services.AddScoped<MeetingToolService>();
services.AddScoped<IAgentGateway, GeminiTaskGateway>();
services.AddScoped<IAgentGateway>(sp => new GeminiTaskGateway(
    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<GeminiSettings>>(),
    sp.GetRequiredService<IQuacktionRepo>(), sp.GetRequiredService<ISessionRepo>(), sp.GetRequiredService<ITranscriptRepo>(),
    sp.GetRequiredService<IMcpConnectionService>(), sp.GetRequiredService<IMcpToolGateway>(), "gemini",
    skillRegistry: sp.GetRequiredService<IMcpSkillRegistry>(), skillExecutor: sp.GetRequiredService<IMcpSkillExecutor>(),
    skillRouter: sp.GetRequiredService<IJevSkillRouter>()));
services.AddSingleton<IGeminiSocketFactory, GeminiSocketFactory>();
services.AddSingleton<GeminiLiveService>();
services.AddSingleton<IConversationalAgentGateway>(sp => sp.GetRequiredService<GeminiLiveService>());
services.AddSingleton<IAudioProviderAdapter>(sp => sp.GetRequiredService<GeminiLiveService>());
services.AddSingleton<IConversationAttachmentGateway>(sp => sp.GetRequiredService<GeminiLiveService>());
services.AddSingleton<DeviceConnectionManager>();
services.AddSingleton<IDeviceConnectionManager>(sp =>
    sp.GetRequiredService<DeviceConnectionManager>()
);
services.AddSingleton<SessionAudioConnectionManager>();
services.AddSingleton<ISessionAudioConnectionManager>(sp =>
    sp.GetRequiredService<SessionAudioConnectionManager>()
);
services.AddScoped<IAudioStreamCoordinator, AudioStreamCoordinator>();
services.AddScoped<IDeviceWebSocketHandler, DeviceWebSocketHandler>();
services.AddScoped<ISessionAudioWebSocketHandler, SessionAudioWebSocketHandler>();
services.AddScoped<ISessionEvents, SessionEvents>();
services.AddHostedService<DatabaseWorker>();
services.AddHostedService<MaintenanceWorker>();
services.AddHostedService<ExecutionWorker>();
services.AddOptions<PreparationSettings>().Bind(builder.Configuration.GetSection("MeetingPreparation"))
    .Validate(s => s.PollSeconds is >= 30 and <= 3600 && s.HorizonDays is >= 1 and <= 90,
        "Meeting preparation polling/horizon limits are invalid.")
    .Validate(s => s.EmbeddingDimensions is 768 or 1536 or 3072 && s.EmbeddingModel == "gemini-embedding-001",
        "Meeting memory embedding configuration must match the provisioned Atlas index.")
    .ValidateOnStart();
services.AddScoped<IMeetingEmbeddingService, GeminiMeetingEmbeddingService>();
services.AddScoped<IMeetingMemorySearchService, MeetingMemorySearchService>();
services.AddScoped<MeetingMemoryIngestion>();
services.AddScoped<IMeetingMemoryLifecycle>(sp => sp.GetRequiredService<MeetingMemoryIngestion>());
services.AddScoped<PreparationStore>();
services.AddScoped<PreparationEmailContext>();
services.AddScoped<RelationshipMemory>();
services.AddScoped<IPreparationContext>(sp => sp.GetRequiredService<PreparationStore>());
services.AddScoped<CalendarPreparationSync>();
services.AddScoped<IPreparationDelivery, NativePreparationDelivery>();
services.AddScoped<PreparationDeliveryWorker>();
services.AddScoped<IMeetingPreparationAgent, MeetingPreparationAgent>();
services.AddScoped<MeetingPreparationResearchWorker>();
services.AddScoped<DeviceNotificationService>();
services.AddScoped<IDeviceNotificationService>(sp => sp.GetRequiredService<DeviceNotificationService>());
services.AddScoped<IBriefingConversation, BriefingConversation>();
services.AddScoped<NotificationSpeech>();
services.AddHostedService<DeviceNotificationWorker>();
services.AddHostedService<MeetingPreparationWorker>();
services.AddHostedService<MeetingAnalysisWorker>();
services.AddHostedService<MeetingRetentionWorker>();
services.AddHostedService<TransportShutdownService>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
}
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.Use(
    async (context, next) =>
    {
        if (
            (
                context.Request.Path.StartsWithSegments("/api")
                || context.Request.Path.StartsWithSegments("/ws")
                || context.Request.Path.StartsWithSegments("/hubs")
            ) && !context.RequestServices.GetRequiredService<DatabaseReadiness>().Ready
        )
            throw new AppException(503, "database_initializing");
        await next();
    }
);
app.UseAuthentication();
app.UseMiddleware<MeetingDeviceAuthenticationMiddleware>();
app.UseAuthorization();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseMiddleware<DeviceWebSocketMiddleware>();
app.UseMiddleware<SessionAudioWebSocketMiddleware>();
app.MapControllers();
app.MapHub<SessionHub>("/hubs/sessions", o => o.CloseOnAuthenticationExpiration = true);
app.Run();

static bool IsValidMcpEncryptionKey(string value)
{
    try { return Convert.FromBase64String(value).Length == 32; }
    catch (FormatException) { return false; }
}

public partial class Program;
