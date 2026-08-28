using Microsoft.Extensions.DependencyInjection;
using VoxOthers.Runtime.Observability;

namespace Microsoft.Extensions.Logging;

/// <summary>Registers the file log in the logging pipeline.</summary>
public static class FileLogExtensions
{
    /// <summary>
    /// Writes the log lines to one file per day, alongside the console.
    /// Options in <c>Logging:File</c>; without the section, defaults are used
    /// (C:\Simulacao\Logs\VoxOthersRuntime\yyyyMMdd_VoxOthersRuntime.log).
    /// </summary>
    public static ILoggingBuilder AddFileLog(this ILoggingBuilder builder)
    {
        builder.Services.AddOptions<FileLogOptions>()
            .BindConfiguration(FileLogOptions.SectionName);

        builder.Services.AddSingleton<ILoggerProvider, FileLogLoggerProvider>();

        return builder;
    }
}
