namespace AWFace.Api.Logging;

public sealed class FileLoggerOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "logs";
    public string FileNamePrefix { get; set; } = "awface-api";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>Arquivos de log mais antigos que este número de dias são removidos (0 desativa).</summary>
    public int RetentionDays { get; set; } = 30;
}
