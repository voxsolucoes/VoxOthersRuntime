using Microsoft.Extensions.Logging;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Texto do nível de log compartilhado entre console e arquivo. Em maiúsculas,
/// para o nível ler claro dentro dos seus colchetes: [INFO], [DEBUG], [ERROR].
/// </summary>
internal static class LogFormat
{
    public static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRITICAL",
        _ => "NONE"
    };
}
