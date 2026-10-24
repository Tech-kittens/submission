using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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
var services = builder.Services;
services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
services.AddEndpointsApiExplorer();
services.AddSwaggerGen();
services.AddProblemDetails();
services.AddExceptionHandler<ExceptionHandler>();
services.Configure<MongoDbSettings>(builder.Configuration.GetSection("MongoDB"));
services.Configure<AudioSettings>(builder.Configuration.GetSection("Audio"));
services.Configure<ElevenLabsSettings>(builder.Configuration.GetSection("ElevenLabs"));
services.Configure<SessionAudioSettings>(builder.Configuration.GetSection("SessionAudio"));
services.Configure<SessionLifecycleSettings>(builder.Configuration.GetSection("Sessions"));
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
                        context.Request.Path.StartsWithSegments("/hubs/sessions")
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
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
services.AddCors(o =>
    o.AddDefaultPolicy(p =>
    {
        if (!builder.Environment.IsDevelopment())
            p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        else if (origins.Length > 0)
            p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
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
services.AddScoped<ITranscriptRepo, TranscriptRepo>();
services.AddScoped<IQuacktionRepo, QuacktionRepo>();
services.AddScoped<IAgentRepo, AgentRepo>();
services.AddScoped<IAccountService, AccountService>();
services.AddScoped<IPasswordHasher<Account>, PasswordHasher<Account>>();
services.AddScoped<ITokenIssuer, JwtTokenIssuer>();
services.Configure<PasswordHasherOptions>(o => o.IterationCount = 210000);
services.AddScoped<IDeviceService, DeviceService>();
services.AddScoped<IDeviceAuthenticationService, DeviceAuthenticationService>();
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
services.AddHttpClient(
    "ElevenLabs",
    c =>
    {
        c.BaseAddress = new Uri("https://api.elevenlabs.io/");
        c.Timeout = TimeSpan.FromSeconds(15);
    }
);
services.AddSingleton<ElevenLabsService>();
services.AddSingleton<IConversationalAgentGateway>(sp =>
    sp.GetRequiredService<ElevenLabsService>()
);
services.AddSingleton<IElevenLabsService>(sp => sp.GetRequiredService<ElevenLabsService>());
services.AddSingleton<IAudioProviderAdapter>(sp => sp.GetRequiredService<ElevenLabsService>());
services.AddSingleton<ConversationEventSink>();
services.AddSingleton<IConversationEventSink>(sp => sp.GetRequiredService<ConversationEventSink>());
services.AddHostedService(sp => sp.GetRequiredService<ConversationEventSink>());
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
services.AddHostedService<TransportShutdownService>();
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 65536);
var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSwagger();
app.UseSwaggerUI();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
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
app.UseAuthorization();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseMiddleware<DeviceWebSocketMiddleware>();
app.UseMiddleware<SessionAudioWebSocketMiddleware>();
app.MapControllers();
app.MapHub<SessionHub>("/hubs/sessions", o => o.CloseOnAuthenticationExpiration = true);
app.Run();

public partial class Program;
