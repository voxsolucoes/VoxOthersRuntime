using System.ComponentModel.DataAnnotations;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Janela de operação (jornada de trabalho) do processamento.
/// </summary>
/// <remarks>
/// Regra de negócio preservada do sistema atual. Fora da janela o
/// processamento pausa, mas a ingestão continua enfileirando — nada se perde,
/// só espera.
///
/// O caso que exige atenção é a jornada que cruza a meia-noite (ex.: 22:00 às
/// 06:00). É onde a implementação ingênua erra, por isso a decisão fica
/// isolada em <see cref="IsWithin"/>, que é lógica pura e testável sem subir
/// o serviço.
/// </remarks>
public sealed class WorkingHoursOptions
{
    /// <summary>Quando desabilitado, o processamento roda 24h.</summary>
    public bool Enabled { get; init; }

    /// <summary>Início da jornada, formato HH:mm.</summary>
    [Required(ErrorMessage = "Runtime:WorkingHours:Start é obrigatório.")]
    public string Start { get; init; } = "00:00";

    /// <summary>Fim da jornada, formato HH:mm.</summary>
    [Required(ErrorMessage = "Runtime:WorkingHours:End é obrigatório.")]
    public string End { get; init; } = "00:00";

    /// <summary>
    /// Tenta interpretar os horários configurados.
    /// </summary>
    public bool TryParse(out TimeOnly start, out TimeOnly end)
    {
        start = default;
        end = default;

        return TimeOnly.TryParse(Start, out start) && TimeOnly.TryParse(End, out end);
    }

    /// <summary>
    /// Indica se o horário informado está dentro da janela de operação.
    /// </summary>
    public bool IsWithin(TimeOnly now)
    {
        if (!Enabled)
        {
            return true;
        }

        if (!TryParse(out var start, out var end))
        {
            // Configuração inválida nunca chega aqui: é barrada no startup pelo
            // validador. Se chegasse, liberar o processamento é a escolha segura
            // — deixar de importar gravação é pior do que importar fora de hora.
            return true;
        }

        // Início igual ao fim significa jornada de 24 horas.
        if (start == end)
        {
            return true;
        }

        // Jornada normal (ex.: 08:00 às 20:00): precisa estar entre as duas.
        if (start < end)
        {
            return now >= start && now < end;
        }

        // Jornada que cruza a meia-noite (ex.: 22:00 às 06:00): vale estar
        // depois do início OU antes do fim.
        return now >= start || now < end;
    }
}
