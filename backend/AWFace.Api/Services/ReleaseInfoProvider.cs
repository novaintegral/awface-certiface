using System.Reflection;

namespace AWFace.Api.Services;

public sealed record ReleaseInfo(
    string Service,
    string Version,
    string Commit,
    string BuildDate,
    string Environment);

public static class ReleaseInfoProvider
{
    public static ReleaseInfo Create(IHostEnvironment environment)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(ReleaseInfoProvider).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var configuredVersion = Environment.GetEnvironmentVariable("AWFACE_VERSION");
        var version = !string.IsNullOrWhiteSpace(configuredVersion)
            ? configuredVersion
            : informationalVersion?.Split('+')[0]
              ?? assembly.GetName().Version?.ToString(3)
              ?? "unknown";

        var configuredCommit = Environment.GetEnvironmentVariable("AWFACE_BUILD_COMMIT");
        var embeddedCommit = informationalVersion?.Split('+').Skip(1).FirstOrDefault();
        var commit = !string.IsNullOrWhiteSpace(configuredCommit)
            ? configuredCommit
            : embeddedCommit ?? "local";

        return new ReleaseInfo(
            "AWFace.Api",
            version,
            commit,
            Environment.GetEnvironmentVariable("AWFACE_BUILD_DATE") ?? "local",
            environment.EnvironmentName);
    }
}
