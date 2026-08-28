using System.ComponentModel.DataAnnotations;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Configuração da quarentena de itens.
/// </summary>
/// <remarks>
/// Separada da quarentena da entrada por pasta, que guarda <b>arquivos</b> de
/// lote recusados na leitura. Esta guarda <b>itens</b> individuais que não
/// puderam ser importados, e um item pode ter chegado por webhook, onde não
/// existe arquivo nenhum.
/// </remarks>
public sealed class QuarantineOptions
{
    public const string SectionName = "Quarantine";

    /// <summary>
    /// Pasta onde os itens recusados são guardados.
    /// </summary>
    /// <remarks>
    /// Não pode ser a mesma da entrada por pasta: o serviço devolve para a
    /// entrada o que encontra na pasta de trabalho no boot, e um item em
    /// quarentena voltaria a ser processado em laço.
    /// </remarks>
    [Required]
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Por quantos dias fica guardada a cópia de um item que já foi reenviado
    /// para processamento.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Vale <b>apenas</b> para a subpasta <c>reprocessados</c>, que guarda o
    /// arquivo do item depois que ele voltou para a fila. Os itens que ainda
    /// aguardam decisão nunca são apagados automaticamente, em nenhuma idade:
    /// eles são dado que não entrou no Vox, e apagar seria perder o
    /// atendimento — exatamente o que a quarentena existe para impedir.
    /// </para>
    /// <para>
    /// A cópia reprocessada é outra coisa: é rastro. Serve para conferir o que
    /// foi reenviado e quando, e para não perder o item se o serviço cair entre
    /// o reenvio e o processamento. Passado esse prazo, ou o item foi importado
    /// ou ele voltou para a quarentena com um arquivo novo — nos dois casos a
    /// cópia antiga não responde mais nada.
    /// </para>
    /// <para>
    /// <b>Zero desliga o expurgo.</b>
    /// </para>
    /// </remarks>
    [Range(0, 3_650, ErrorMessage = "Quarantine:ReprocessedRetentionDays deve estar entre 0 e 3650.")]
    public int ReprocessedRetentionDays { get; init; } = 30;
}
