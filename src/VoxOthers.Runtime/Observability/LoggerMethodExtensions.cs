using System.Runtime.CompilerServices;

namespace Microsoft.Extensions.Logging;

/// <summary>
/// Ponto de captura do método de chamada. <see cref="Here"/> devolve um
/// <see cref="LogSite"/> e as chamadas seguem como <c>_logger.Here().Info(...)</c>:
/// o nome do método é anexado a toda linha em tempo de compilação, via
/// [CallerMemberName] (confiável também em código assíncrono, ao contrário de
/// percorrer a pilha). O método vira o primeiro placeholder do template
/// ("{Method}: ...") e é exposto como campo estruturado chamado "Method".
/// </summary>
public static class LoggerMethodExtensions
{
    public static LogSite Here(this ILogger logger, [CallerMemberName] string? method = null)
        => new LogSite(logger, method);
}

/// <summary>
/// Ponto de escrita do log já com o método de chamada anexado. As sobrecargas
/// com parâmetro de exceção registram a exceção junto.
/// </summary>
public readonly struct LogSite
{
    private readonly ILogger _logger;
    private readonly string? _method;

    public LogSite(ILogger logger, string? method)
    {
        _logger = logger;
        _method = method;
    }

    public void Info(string message, params object?[] args)
        => _logger.LogInformation(Template(_method, message), Args(_method, args));

    public void Info(Exception? exception, string message, params object?[] args)
        => _logger.LogInformation(exception, Template(_method, message), Args(_method, args));

    public void Debug(string message, params object?[] args)
        => _logger.LogDebug(Template(_method, message), Args(_method, args));

    public void Debug(Exception? exception, string message, params object?[] args)
        => _logger.LogDebug(exception, Template(_method, message), Args(_method, args));

    public void Warn(string message, params object?[] args)
        => _logger.LogWarning(Template(_method, message), Args(_method, args));

    public void Warn(Exception? exception, string message, params object?[] args)
        => _logger.LogWarning(exception, Template(_method, message), Args(_method, args));

    public void Error(string message, params object?[] args)
        => _logger.LogError(Template(_method, message), Args(_method, args));

    public void Error(Exception? exception, string message, params object?[] args)
        => _logger.LogError(exception, Template(_method, message), Args(_method, args));

    private static string Template(string? method, string message)
        => string.IsNullOrEmpty(method) ? message : "{Method}: " + message;

    private static object?[] Args(string? method, object?[] args)
    {
        if (string.IsNullOrEmpty(method))
        {
            return args;
        }

        var values = new object?[args.Length + 1];
        values[0] = method;
        if (args.Length > 0)
        {
            Array.Copy(args, 0, values, 1, args.Length);
        }

        return values;
    }
}
