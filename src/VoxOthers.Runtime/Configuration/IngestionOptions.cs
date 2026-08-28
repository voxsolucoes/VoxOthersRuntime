using System.ComponentModel.DataAnnotations;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Configuração das duas formas de entrada de dados.
/// </summary>
/// <remarks>
/// Pasta e webhook são apenas adaptadores: os dois entregam no mesmo canal
/// interno e daí em diante o processamento é único (AD-3). Por isso a
/// configuração de cada um cobre só a captura — nada de regra de negócio.
/// </remarks>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    [Required]
    public FolderIngestionOptions Folder { get; init; } = new();

    [Required]
    public WebhookIngestionOptions Webhook { get; init; } = new();
}

/// <summary>
/// Entrada por pasta monitorada.
/// </summary>
public sealed class FolderIngestionOptions
{
    public bool Enabled { get; init; }

    /// <summary>Pastas vigiadas em busca de arquivos novos.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>
    /// Pasta onde o arquivo fica enquanto está sendo processado.
    /// </summary>
    /// <remarks>
    /// O arquivo sai da entrada assim que é aceito e só vai para
    /// <see cref="ProcessedPath"/> quando o lote termina de verdade. Serve a
    /// dois propósitos: a mesma entrada não é lida duas vezes, e um arquivo
    /// que ficou aqui depois de uma queda é prova de trabalho interrompido —
    /// no próximo boot ele volta para a entrada e é reprocessado.
    /// </remarks>
    public string WorkingPath { get; init; } = string.Empty;

    /// <summary>Destino dos itens que falharam, com o motivo ao lado.</summary>
    public string QuarantinePath { get; init; } = string.Empty;

    /// <summary>Destino dos arquivos já processados com sucesso.</summary>
    public string ProcessedPath { get; init; } = string.Empty;

    /// <summary>Máscara dos arquivos considerados na varredura.</summary>
    public string FilePattern { get; init; } = "*.json";

    /// <summary>
    /// Intervalo da varredura.
    /// </summary>
    /// <remarks>
    /// A detecção é por varredura periódica, e não por evento do sistema de
    /// arquivos. O evento é imediato, mas perde notificação sob carga e é
    /// notoriamente pouco confiável em unidade de rede — que é justamente onde
    /// estas pastas costumam ficar. Como a varredura teria de existir de
    /// qualquer jeito como rede de proteção, manter só ela troca latência por
    /// um comportamento previsível e simples de testar.
    /// </remarks>
    [Range(1, 3_600, ErrorMessage = "Ingestion:Folder:ScanIntervalSeconds deve estar entre 1 e 3600.")]
    public int ScanIntervalSeconds { get; init; } = 30;
}

/// <summary>
/// Entrada por webhook.
/// </summary>
public sealed class WebhookIngestionOptions
{
    public bool Enabled { get; init; }

    /// <summary>Caminho HTTP que recebe os lotes.</summary>
    public string Path { get; init; } = "/api/v1/centralize";

    /// <summary>
    /// Limite de itens por lote.
    /// </summary>
    /// <remarks>
    /// Protege contra um backend mal comportado mandar um lote gigante e
    /// consumir toda a fila interna de uma vez.
    /// </remarks>
    [Range(1, 10_000, ErrorMessage = "Ingestion:Webhook:MaxBatchSize deve estar entre 1 e 10000.")]
    public int MaxBatchSize { get; init; } = 500;

    /// <summary>
    /// Exige chave de acesso em cada envio.
    /// </summary>
    /// <remarks>
    /// Ligado por padrão. Desligar é decisão consciente, para ambiente de
    /// desenvolvimento: sem a chave, qualquer um que alcance a porta consegue
    /// injetar gravação no Vox.
    /// </remarks>
    public bool RequireApiKey { get; init; } = true;

    /// <summary>
    /// Chave de acesso por origem: nome do backend emissor para a chave dele.
    /// </summary>
    /// <remarks>
    /// Uma chave por origem, e não uma só para todo mundo. É o que permite
    /// revogar o acesso de um backend específico sem parar os outros, e o que
    /// garante que o campo de origem do lote é confiável — ele passa a ser
    /// conferido contra a chave, em vez de ser aceito no que o emissor disser.
    /// </remarks>
    public IReadOnlyDictionary<string, string> ApiKeys { get; init; }
        = new Dictionary<string, string>();
}
