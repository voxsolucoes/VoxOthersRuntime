using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Formatter de console no mesmo formato do arquivo:
/// [yyyy-MM-dd HH:mm:ss.fff] [NIVEL] categoria: mensagem
/// O SimpleConsoleFormatter não permite configurar o template da linha, então um
/// formatter próprio garante que console e arquivo saiam idênticos.
/// </summary>
internal sealed class VoxConsoleFormatter : ConsoleFormatter
{
    private const string TimestampFormat = "[yyyy-MM-dd HH:mm:ss.fff] ";

    public VoxConsoleFormatter() : base("vox")
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var line = new StringBuilder();

        line.Append(DateTimeOffset.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture));
        line.Append('[').Append(LogFormat.LevelText(logEntry.LogLevel)).Append("] ");
        line.Append(logEntry.Category);
        line.Append(": ");

        if (scopeProvider is not null)
        {
            scopeProvider.ForEachScope<object?>((scope, _) =>
            {
                if (scope is not null)
                {
                    line.Append(scope).Append(' ');
                }
            }, null);
        }

        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (!string.IsNullOrEmpty(message))
        {
            line.Append(message);
        }

        if (logEntry.Exception is not null)
        {
            line.Append(' ').Append(logEntry.Exception);
        }

        textWriter.WriteLine(line.ToString());
    }
}
