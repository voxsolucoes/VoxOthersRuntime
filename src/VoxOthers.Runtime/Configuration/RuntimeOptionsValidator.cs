using Microsoft.Extensions.Options;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Validações do bloco Runtime que não cabem em atributo.
/// </summary>
public sealed class RuntimeOptionsValidator : IValidateOptions<RuntimeOptions>
{
    public ValidateOptionsResult Validate(string? name, RuntimeOptions options)
    {
        var failures = new List<string>();

        var hours = options.WorkingHours;

        if (hours.Enabled && !hours.TryParse(out _, out _))
        {
            failures.Add(
                $"Runtime:WorkingHours — horário inválido (Start='{hours.Start}', End='{hours.End}'). " +
                "Use o formato HH:mm, por exemplo '08:00' e '20:00'.");
        }

        // Fila menor que o número de workers não quebra, mas indica configuração
        // sem sentido: os workers ficariam disputando uma fila que nunca acumula.
        if (options.ChannelCapacity < options.WorkerCount)
        {
            failures.Add(
                $"Runtime:ChannelCapacity ({options.ChannelCapacity}) não pode ser menor que " +
                $"Runtime:WorkerCount ({options.WorkerCount}).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
