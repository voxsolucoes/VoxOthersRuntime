using System.ComponentModel.DataAnnotations;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Configuração de processamento do Runtime.
/// </summary>
public sealed class RuntimeOptions
{
    public const string SectionName = "Runtime";

    /// <summary>
    /// Quantidade de workers consumindo a fila interna.
    /// </summary>
    /// <remarks>
    /// Default 4 e não "número de núcleos": o gargalo aqui é I/O de disco e
    /// rede, não CPU. Concorrência alta sobre o mesmo volume de disco degrada
    /// o throughput em vez de melhorar. Aumentar só com medição.
    /// </remarks>
    [Range(1, 64, ErrorMessage = "Runtime:WorkerCount deve estar entre 1 e 64.")]
    public int WorkerCount { get; init; } = 4;

    /// <summary>
    /// Capacidade máxima da fila interna entre ingestão e processamento.
    /// </summary>
    /// <remarks>
    /// É o que garante memória limitada por configuração, e não pelo volume
    /// que a origem resolver mandar. Cheia, a ingestão espera (backpressure).
    /// </remarks>
    [Range(1, 100_000, ErrorMessage = "Runtime:ChannelCapacity deve estar entre 1 e 100000.")]
    public int ChannelCapacity { get; init; } = 1_000;

    [Required]
    public WorkingHoursOptions WorkingHours { get; init; } = new();

    [Required]
    public RetryOptions Retry { get; init; } = new();

    [Required]
    public OperationDefaultsOptions OperationDefaults { get; init; } = new();
}

/// <summary>
/// Política de nova tentativa para falhas transitórias.
/// </summary>
public sealed class RetryOptions
{
    [Range(0, 10, ErrorMessage = "Runtime:Retry:MaxAttempts deve estar entre 0 e 10.")]
    public int MaxAttempts { get; init; } = 3;

    [Range(1, 300, ErrorMessage = "Runtime:Retry:BaseDelaySeconds deve estar entre 1 e 300.")]
    public int BaseDelaySeconds { get; init; } = 5;
}

/// <summary>
/// Comportamentos padrão de importação, aplicáveis a todas as operações.
/// </summary>
public sealed class OperationDefaultsOptions
{
    /// <summary>Descarta gravação com duração zero em vez de importá-la.</summary>
    public bool DiscardZeroDuration { get; init; } = true;

    /// <summary>
    /// Tempo de espera até considerar que um arquivo de mídia não vai mais
    /// chegar (ou terminar de ser gravado) e mandar o item para quarentena.
    /// </summary>
    [Range(0, 3_600, ErrorMessage = "Runtime:OperationDefaults:MediaWaitSeconds deve estar entre 0 e 3600.")]
    public int MediaWaitSeconds { get; init; } = 60;
}
