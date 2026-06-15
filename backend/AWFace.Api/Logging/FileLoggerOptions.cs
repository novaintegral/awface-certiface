namespace AWFace.Api.Logging;

public sealed class FileLoggerOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "logs";
    public string FileNamePrefix { get; set; } = "awface-api";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;
}
