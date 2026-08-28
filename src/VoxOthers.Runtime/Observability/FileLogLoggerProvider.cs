using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// File-log provider: writes the same lines as the console to one file per
/// day, following the current services' convention. Formatting is local (does
/// not reuse SimpleConsoleFormatter) because the format is small and must be
/// identical between console and file.
/// </summary>
public sealed class FileLogLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly FileLogOptions _options;
    private readonly string _directory;
    private readonly string _fileName;
    private readonly string _extension;
    private readonly object _syncLock = new();

    private IExternalScopeProvider _scopeProvider = EmptyScopeProvider.Instance;
    private StreamWriter? _writer;
    private string? _currentFile;
    private bool _disposed;

    public FileLogLoggerProvider(IOptions<FileLogOptions> options)
    {
        _options = options.Value;
        _directory = string.IsNullOrWhiteSpace(_options.Path)
            ? throw new InvalidOperationException(
                "Logging:File:Path cannot be empty. Set the file-log folder.")
            : _options.Path;
        _fileName = string.IsNullOrWhiteSpace(_options.FileName) ? "VoxOthersRuntime" : _options.FileName;
        _extension = string.IsNullOrWhiteSpace(_options.Extension) ? "log" : _options.Extension;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider)
    {
        lock (_syncLock)
        {
            _scopeProvider = scopeProvider ?? EmptyScopeProvider.Instance;
        }
    }

    /// <summary>Own minimum level (Logging:File:MinimumLevel), if configured.</summary>
    internal bool IsLevelEnabled(LogLevel level)
        => _options.MinimumLevel is null || level >= _options.MinimumLevel.Value;

    internal void Write<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (_disposed)
        {
            return;
        }

        var file = LogFilePath();
        lock (_syncLock)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(file);
                WriteLine(category, level, eventId, state, exception, formatter);
                _writer!.Flush();
            }
            catch (IOException)
            {
                // File open by another process with an exclusive lock, or disk
                // full. The line is lost, the process goes on — logging never
                // brings down what it is logging.
            }
            catch (UnauthorizedAccessException)
            {
                // Folder without write permission. Same treatment as above.
            }
        }
    }

    private string LogFilePath()
        => Path.Combine(_directory, $"{DateTime.Now:yyyyMMdd}_{_fileName}.{_extension}");

    private void EnsureWriter(string file)
    {
        if (_writer != null && string.Equals(_currentFile, file, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _writer?.Dispose();
        Directory.CreateDirectory(_directory);
        var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        _currentFile = file;
    }

    private void WriteLine<TState>(
        string category,
        LogLevel level,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var line = new StringBuilder();

        if (!string.IsNullOrEmpty(_options.TimestampFormat))
        {
            var now = _options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
            line.Append(now.ToString(_options.TimestampFormat, CultureInfo.InvariantCulture));
        }

        line.Append('[').Append(LogFormat.LevelText(level)).Append("] ");
        line.Append(category);
        line.Append(": ");

        if (_options.IncludeScopes)
        {
            _scopeProvider.ForEachScope<object?>((scope, _) =>
            {
                if (scope is not null)
                {
                    line.Append(scope).Append(' ');
                }
            }, null);
        }

        var message = formatter(state, exception);
        if (!string.IsNullOrEmpty(message))
        {
            line.Append(message);
        }

        if (exception is not null)
        {
            line.Append(' ').Append(exception);
        }

        _writer!.WriteLine(line.ToString());
    }

    public void Dispose()
    {
        lock (_syncLock)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    /// <summary>Logger of a provider — just forwards to the provider to write.</summary>
    private sealed class FileLogLogger : ILogger
    {
        private readonly FileLogLoggerProvider _provider;
        private readonly string _category;

        public FileLogLogger(FileLogLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
            => InertScope.Instance;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && _provider.IsLevelEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            _provider.Write(_category, logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>
    /// Empty scope while the factory has not injected the real provider yet.
    /// Nobody logs before that, so there is nothing to enumerate.
    /// </summary>
    private sealed class EmptyScopeProvider : IExternalScopeProvider
    {
        public static readonly EmptyScopeProvider Instance = new();

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
        {
        }

        public IDisposable Push(object? state) => InertScope.Instance;
    }

    /// <summary>Empty disposable scope, for the scope methods that are never called.</summary>
    private sealed class InertScope : IDisposable
    {
        public static readonly InertScope Instance = new();

        public void Dispose()
        {
        }
    }
}
