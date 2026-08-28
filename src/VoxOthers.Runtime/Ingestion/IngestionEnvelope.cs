using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// Um lote aceito, a caminho do processamento.
/// </summary>
/// <remarks>
/// É o que trafega no canal interno. Repare que a pasta e o webhook produzem
/// exatamente este mesmo objeto: a partir daqui não existe mais "veio de tal
/// jeito", e o processamento é um só. Essa convergência é o que impede o
/// problema clássico de dois caminhos que se comportam diferente.
/// </remarks>
public sealed class IngestionEnvelope
{
    /// <summary>
    /// Identificador do lote dentro do Runtime, para correlacionar o log.
    /// </summary>
    /// <remarks>
    /// Gerado aqui, e não recebido do emissor. Vindo de fora seria opcional na
    /// prática (backend de terceiro esquece), poderia repetir entre origens
    /// diferentes e não poderia ser confiado justamente onde mais importa: no
    /// rastreamento de um incidente.
    /// </remarks>
    public required string BatchId { get; init; }

    /// <summary>Por onde o lote entrou.</summary>
    public required IngestionOrigin Origin { get; init; }

    /// <summary>O conteúdo, já lido e conferido no envelope.</summary>
    public required CentralizeBatch Batch { get; init; }

    /// <summary>
    /// Arquivo correspondente na pasta de trabalho. Nulo quando o lote chegou
    /// pelo webhook, em que não há arquivo nenhum.
    /// </summary>
    public string? WorkingFilePath { get; init; }

    /// <summary>Momento em que o Runtime aceitou o lote.</summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Origem declarada pelo emissor.</summary>
    public string Source => Batch.Source;

    /// <summary>Quantidade de atendimentos no lote.</summary>
    public int ItemCount => Batch.Items.Count;
}

/// <summary>Forma de entrada de um lote.</summary>
public enum IngestionOrigin
{
    /// <summary>Arquivo depositado em pasta monitorada.</summary>
    Folder = 0,

    /// <summary>Envio direto pela rede.</summary>
    Webhook = 1,

    /// <summary>
    /// Item devolvido da quarentena para uma nova tentativa.
    /// </summary>
    /// <remarks>
    /// Vale a pena distinguir no log: um lote que reaparece é reprocessamento
    /// pedido por alguém, e não a origem mandando o mesmo dado duas vezes.
    /// </remarks>
    Quarantine = 2
}
