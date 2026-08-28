using System.ComponentModel.DataAnnotations;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Configuração da saída em bilhete <c>.GRF</c>.
/// </summary>
public sealed class GrfOptions
{
    public const string SectionName = "Grf";

    /// <summary>
    /// Pasta de registro — onde o serviço de importação do Vox procura os
    /// bilhetes. É a fronteira entre este projeto e o resto do Vox.
    /// </summary>
    /// <remarks>
    /// Vale para <b>todos</b> os tipos de bilhete, e não só para o <c>.GRF</c>.
    /// O REGBD lê qualquer bilhete que apareça aqui e decide o que fazer pelo
    /// próprio conteúdo, então voz e texto dividem a mesma pasta e se distinguem
    /// pela extensão. Não há uma pasta por formato.
    /// </remarks>
    [Required]
    public string RegisterPath { get; init; } = string.Empty;

    /// <summary>
    /// Pasta de trabalho, onde o bilhete é escrito antes de ir para a de
    /// registro.
    /// </summary>
    /// <remarks>
    /// Existe por dois motivos, e nenhum é organização. Primeiro: o importador
    /// nunca enxerga bilhete pela metade — o arquivo só aparece na pasta de
    /// registro quando já está inteiro em disco. Segundo: é o que permite dois
    /// workers gerarem bilhete ao mesmo tempo sem disputarem nome de arquivo.
    /// </remarks>
    [Required]
    public string WorkPath { get; init; } = string.Empty;

    /// <summary>
    /// Raiz da árvore de gravação do Vox — a pasta que contém <c>aaaa\MM\dd</c>.
    /// </summary>
    /// <remarks>
    /// É para dentro dela que a mídia é levada antes do bilhete ser gerado. O
    /// campo <c>CA</c> do bilhete é o caminho a partir daqui, então esta pasta
    /// e a que o Vox tem configurada como raiz de gravação têm de ser a mesma:
    /// se divergirem, o bilhete sai correto e a gravação nunca é encontrada.
    /// </remarks>
    [Required]
    public string RecordingRoot { get; init; } = string.Empty;

    /// <summary>
    /// Nome do servidor Vox, como deve sair no campo <c>SR</c> do bilhete.
    /// </summary>
    /// <remarks>
    /// Vem de configuração, e não do contrato, porque identifica a instalação
    /// do Vox que recebe as gravações — é a mesma para todos os lotes que este
    /// serviço processa, independentemente de quem os enviou.
    /// </remarks>
    [Required]
    public string ServerName { get; init; } = string.Empty;
}
