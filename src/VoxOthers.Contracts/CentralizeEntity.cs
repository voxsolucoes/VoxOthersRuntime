using System.Text.Json.Serialization;

namespace VoxOthers.Contracts;

/// <summary>
/// Um atendimento já normalizado, pronto para ser importado no Vox.
/// </summary>
/// <remarks>
/// <para>
/// Este é o contrato oficial entre o Builder e o Vox Others Runtime. Tudo o
/// que o Runtime precisa saber sobre um atendimento está aqui — ele não
/// consulta o sistema de origem, não interpreta formato de fornecedor e não
/// tem regra específica de cliente.
/// </para>
/// <para>
/// As propriedades são somente-leitura após a criação. Um registro que já
/// entrou na fila não muda de conteúdo no meio do caminho, o que elimina uma
/// classe inteira de defeito difícil de reproduzir quando vários trabalhadores
/// processam em paralelo.
/// </para>
/// <para>
/// A única exceção é <see cref="UniqueId"/>: a leitura do lote a preenche
/// quando a origem não enviou identificador (ver <see cref="EnsureUniqueId"/>),
/// antes de o registro entrar na fila. Daí em diante ele volta a ser imutável.
/// </para>
/// </remarks>
public sealed class CentralizeEntity
{
    /// <summary>
    /// Identificador do atendimento no sistema de origem.
    /// </summary>
    /// <remarks>
    /// Tem três papéis, e por isso é o campo mais crítico do contrato:
    /// evita importar o mesmo atendimento duas vezes, compõe o nome do
    /// arquivo do bilhete e é gravado nos dados livres como
    /// <c>UNIQUEIDOTHERS</c>. Por virar nome de arquivo, não aceita
    /// caracteres proibidos pelo sistema de arquivos.
    /// <para>
    /// Não é obrigatório na origem: quando chega vazio, a leitura do lote
    /// preenche um GUID (ver <see cref="EnsureUniqueId"/>), mantendo o rastro
    /// do atendimento mesmo quando o backend não informou o id.
    /// </para>
    /// </remarks>
    public string UniqueId { get; set; } = string.Empty;

    /// <summary>
    /// Garante um identificador ao registro: quando a origem não enviou, gera
    /// um GUID (v7, ordenado por tempo) no formato compacto — seguro como nome
    /// de arquivo e único o bastante para a deduplicação e o
    /// <c>UNIQUEIDOTHERS</c>.
    /// </summary>
    public void EnsureUniqueId()
    {
        if (string.IsNullOrWhiteSpace(UniqueId))
        {
            UniqueId = Guid.CreateVersion7().ToString("N");
        }
    }

    /// <summary>
    /// Servidor Vox de destino.
    /// </summary>
    /// <remarks>
    /// Junto de <see cref="OperationId"/>, é o que diz para onde o
    /// atendimento vai. Todo o cadastro no Vox — busca de ramal, número do
    /// canal, verificação de ramal ativo — é feito dentro de um servidor;
    /// sem esse campo o Runtime teria de adivinhar, e adivinhar errado
    /// significa gravação importada no lugar errado.
    /// </remarks>
    public int ServerId { get; init; }

    /// <summary>
    /// Operação à qual o atendimento pertence.
    /// </summary>
    /// <remarks>
    /// Obrigatório. Cada backend gerado pelo Builder atende a uma operação
    /// conhecida, então a informação sempre existe na origem. Deixá-la a
    /// cargo de um padrão na configuração do Runtime criaria um segundo lugar
    /// para a mesma verdade — e um erro silencioso no dia em que o padrão não
    /// combinasse com o backend.
    /// </remarks>
    public int OperationId { get; init; }

    /// <summary>Natureza do atendimento: voz ou texto.</summary>
    public MediaKind Kind { get; init; } = MediaKind.Call;

    /// <summary>
    /// Login do operador no sistema de origem. É a forma preferida de
    /// identificar quem atendeu.
    /// </summary>
    public string? AgentLogin { get; init; }

    /// <summary>
    /// Nome do operador. Usado quando a origem não expõe login, e como
    /// desempate junto ao ramal.
    /// </summary>
    public string? AgentName { get; init; }

    /// <summary>
    /// Ramal/posição do operador, quando a origem informa. Não é
    /// obrigatório, mas melhora muito a identificação em bases com
    /// operadores homônimos.
    /// </summary>
    public string? Extension { get; init; }

    /// <summary>Número do contato (quem ligou ou para quem se ligou).</summary>
    public string? Ani { get; init; }

    /// <summary>Nome do contato, quando conhecido.</summary>
    public string? ContactName { get; init; }

    /// <summary>Sentido do atendimento.</summary>
    public CallDirection Direction { get; init; } = CallDirection.Unknown;

    /// <summary>
    /// Início do atendimento, com fuso.
    /// </summary>
    /// <remarks>
    /// O fuso é parte do contrato de propósito: o Builder pode rodar em
    /// máquina, região ou contêiner com fuso diferente do servidor Vox.
    /// Trafegar sem fuso é a origem clássica de gravação com três horas de
    /// diferença. A conversão para a hora local do servidor acontece na
    /// geração do bilhete, em um único ponto.
    /// </remarks>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Duração em segundos.</summary>
    public int DurationSeconds { get; init; }

    /// <summary>
    /// Caminho completo do arquivo de mídia, acessível pelo Runtime.
    /// Obrigatório para atendimento de voz.
    /// </summary>
    public string? MediaPath { get; init; }

    /// <summary>
    /// Mensagens do atendimento de texto, em ordem cronológica.
    /// </summary>
    public IReadOnlyList<ChatMessage> Messages { get; init; } = [];

    /// <summary>Arquivos adicionais ligados ao atendimento.</summary>
    public IReadOnlyList<MediaAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Campos livres do cliente (protocolo, CPF, motivo, o que for).
    /// </summary>
    /// <remarks>
    /// Cada par vira uma marcação nos dados livres do bilhete, com a chave
    /// como nome da marcação. O Runtime ajusta a chave sozinho quando ela não
    /// serve como nome de marcação — espaço e ponto viram sublinhado, acento
    /// é retirado —, de modo que o backend não precisa conhecer a regra.
    /// Ver <see cref="CentralizeValidator.NormalizeExtensionKey"/>.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Extensions { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Fim do atendimento, derivado do início e da duração. Não trafega no
    /// JSON: é cálculo, não informação da origem, e mandá-lo abriria espaço
    /// para um lote chegar com início, duração e fim que não fecham entre si.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset EndedAt => StartedAt.AddSeconds(DurationSeconds);
}
