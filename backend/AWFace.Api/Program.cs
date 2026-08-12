using System.Text.Json.Serialization;
using AWFace.Api.Configuration;
using AWFace.Api.Data;
using AWFace.Api.Endpoints;
using AWFace.Api.Logging;
using AWFace.Api.Security;
using AWFace.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AwfaceOptions>(builder.Configuration.GetSection("Awface"));
builder.Services.Configure<CertifaceOptions>(builder.Configuration.GetSection("Certiface"));
builder.Services.Configure<FileLoggerOptions>(builder.Configuration.GetSection("Logging:File"));

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var allowedOrigins = builder.Configuration.GetSection("Awface:Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Angular", policy =>
    {
        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<SensitiveDataProtector>();
builder.Services.AddScoped<AwfaceDb>();
builder.Services.AddScoped<AwfaceRepository>();
builder.Services.AddScoped<AwfaceSchemaInitializer>();
builder.Services.AddSingleton<FaceAssetStorage>();
builder.Services.AddSingleton<TenantWebhookClient>();
builder.Services.AddHttpClient<CertifaceClient>();
builder.Services.AddHttpClient("TenantCallback");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();

var app = builder.Build();
var releaseInfo = ReleaseInfoProvider.Create(app.Environment);

await using (var scope = app.Services.CreateAsyncScope())
{
    var schemaInitializer = scope.ServiceProvider.GetRequiredService<AwfaceSchemaInitializer>();
    await schemaInitializer.InitializeAsync(CancellationToken.None);
}

app.UseCors("Angular");

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
