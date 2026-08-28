using Microsoft.Extensions.Logging;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// File-log options, read from the <c>Logging:File</c> appsettings section.
/// Defaults follow the current services' convention: one file per day,
/// <c>yyyyMMdd_ServiceName.log</c>, in a per-system folder inside
/// <c>C:\Simulacao\Logs</c>.
/// </summary>
public sealed class FileLogOptions
{
    public const string SectionName = "Logging:File";

    /// <summary>Folder where the log files are written.</summary>
    public string Path { get; set; } = @"C:\Simulacao\Logs\VoxOthersRuntime";

    /// <summary>Base file name; the date comes first (yyyyMMdd_...).</summary>
    public string FileName { get; set; } = "VoxOthersRuntime";

    /// <summary>Log file extension.</summary>
    public string Extension { get; set; } = "log";

    /// <summary>
    /// Minimum level written to file. Empty delegates to the global
    /// <c>Logging:LogLevel</c> — the same filter the console uses, so file and
    /// console never diverge on purpose.
    /// </summary>
    public LogLevel? MinimumLevel { get; set; }

    /// <summary>Includes scope in each line (BatchId, Source, ...), like the console.</summary>
    public bool IncludeScopes { get; set; } = true;

    /// <summary>One line per entry, in the same format as the console.</summary>
    public bool SingleLine { get; set; } = true;

    /// <summary>False uses local time; true uses UTC.</summary>
    public bool UseUtcTimestamp { get; set; }

    /// <summary>Timestamp format, brackets included. Ends in a space, like the console.</summary>
    public string TimestampFormat { get; set; } = "[yyyy-MM-dd HH:mm:ss.fff] ";
}
