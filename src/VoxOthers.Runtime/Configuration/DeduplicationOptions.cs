using System.ComponentModel.DataAnnotations;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Configuração da deduplicação de itens.
/// </summary>
/// <remarks>
/// <para>
/// A deduplicação vive em disco, e não em tabela: o Runtime não cria estrutura
/// na base do Vox, que é compartilhada com o sistema atual durante toda a
/// migração. Ver AD-5.
/// </para>
/// <para>
/// A pasta precisa ser <b>a mesma para todas as instâncias</b> do serviço. É ela
/// que faz duas instâncias enxergarem uma à outra; apontando cada uma para um
/// disco local, cada uma deduplica só o que ela própria importou e o mesmo
/// atendimento entra duas vezes.
/// </para>
/// </remarks>
public sealed class DeduplicationOptions
{
    public const string SectionName = "Deduplication";

    /// <summary>
    /// Pasta onde ficam os marcadores dos itens já importados.
    /// </summary>
    [Required]
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Por quantos dias o marcador de um item importado é guardado.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Por que expurgar.</b> É um arquivo por atendimento importado, para
    /// sempre. Numa operação de porte médio isso passa de um milhão de arquivos
    /// no primeiro ano — e o custo não é o espaço (cada marcador tem algumas
    /// dezenas de bytes), é a pasta: backup, antivírus e qualquer listagem
    /// passam a levar minutos, e a pasta fica em unidade de rede.
    /// </para>
    /// <para>
    /// <b>Por que 90 dias.</b> O marcador só serve enquanto o mesmo atendimento
    /// ainda puder chegar de novo. Apagar cedo demais é o único risco real
    /// aqui: reenviado depois do expurgo, o item volta a parecer novo e é
    /// importado outra vez. Noventa dias é folgado em relação a qualquer
    /// reprocessamento de origem que já vimos, e continua limitando a pasta a
    /// um trimestre de histórico.
    /// </para>
    /// <para>
    /// <b>Zero desliga o expurgo</b>, para quem preferir guardar tudo e cuidar
    /// da pasta por fora.
    /// </para>
    /// </remarks>
    [Range(0, 3_650, ErrorMessage = "Deduplication:RetentionDays deve estar entre 0 e 3650.")]
    public int RetentionDays { get; init; } = 90;
}
