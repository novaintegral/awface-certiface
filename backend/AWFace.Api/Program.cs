using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using AWFace.Api.Configuration;
using AWFace.Api.Data;
using AWFace.Api.Endpoints;
using AWFace.Api.Logging;
using AWFace.Api.Security;
using AWFace.Api.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AwfaceOptions>(builder.Configuration.GetSection("Awface"));
builder.Services.Configure<CertifaceOptions>(builder.Configuration.GetSection("Certiface"));
builder.Services.Configure<FileLoggerOptions>(builder.Configuration.GetSection("Logging:File"));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var awfaceOptions = builder.Configuration.GetSection("Awface").Get<AwfaceOptions>() ?? new AwfaceOptions();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy =>
    {
        policy.WithOrigins(awfaceOptions.Cors.AllowedOrigins)
            .WithHeaders("Content-Type", "Authorization", EndpointAccess.JourneyTokenHeader)
            .WithMethods("GET", "POST", "PATCH");
    });
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // O Nginx do compose é o único proxy confiável; a API não deve ficar exposta diretamente quando isto estiver ativo.
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
    options.ForwardLimit = 1;
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AdminEndpoints.LoginRateLimitPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(httpContext),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy(JourneyLaunchEndpoints.PublicRateLimitPolicy, httpContext => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey(httpContext),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

builder.Services.AddSingleton<SensitiveDataProtector>();
builder.Services.AddSingleton<SignedTokenService>();
builder.Services.AddSingleton<CallbackOutboxSignal>();
builder.Services.AddScoped<AwfaceDb>();
builder.Services.AddScoped<AwfaceRepository>();
builder.Services.AddScoped<AwfaceSchemaInitializer>();
builder.Services.AddScoped<SensitiveDataMigrator>();
builder.Services.AddScoped<AdminBootstrapper>();
builder.Services.AddScoped<CallbackOutbox>();
builder.Services.AddScoped<SubmissionDependencies>();
builder.Services.AddSingleton<FaceAssetStorage>();
builder.Services.AddSingleton<TenantWebhookClient>();
builder.Services.AddHostedService<CallbackDispatcher>();
builder.Services.AddHttpClient<CertifaceClient>();
builder.Services.AddHttpClient("TenantCallback");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();

var app = builder.Build();
var releaseInfo = ReleaseInfoProvider.Create(app.Environment);

StartupSecurityValidator.Validate(
    app.Services.GetRequiredService<IOptions<AwfaceOptions>>().Value,
    app.Environment,
    app.Logger
);

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider.GetRequiredService<AwfaceSchemaInitializer>().InitializeAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<SensitiveDataMigrator>().MigrateAsync(CancellationToken.None);
    await scope.ServiceProvider.GetRequiredService<AdminBootstrapper>().EnsureAdminAsync(CancellationToken.None);
}

if (awfaceOptions.TrustForwardedHeaders)
{
    app.UseForwardedHeaders();
}

app.UseExceptionHandler();
app.UseCors("Angular");
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "AWFace.Api",
    version = releaseInfo.Version,
    commit = releaseInfo.Commit,
    utc = DateTimeOffset.UtcNow
})).WithTags("Health");

app.MapGet("/version", () => Results.Ok(releaseInfo)).WithTags("Release");
app.MapGet("/api/awface/version", () => Results.Ok(releaseInfo)).WithTags("Release");

app.MapAdminEndpoints();
app.MapJourneyLaunchEndpoints();
app.MapJourneyEndpoints();
app.MapFacetecEndpoints();

app.Run();

static string ClientKey(HttpContext httpContext)
{
    return httpContext.Connection.RemoteIpAddress?.ToString() ?? IPAddress.None.ToString();
}
