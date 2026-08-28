namespace VoxOthers.Contracts;

/// <summary>
/// Uma mensagem dentro de um atendimento de texto.
/// </summary>
public sealed class ChatMessage
{
    /// <summary>Momento em que a mensagem foi enviada.</summary>
    public DateTimeOffset SentAt { get; init; }

    /// <summary>Quem enviou: o agente ou o contato.</summary>
    public ChatAuthor Author { get; init; }

    /// <summary>
    /// Nome de quem enviou, como exibido na origem. Opcional — serve para
    /// apresentação, não para identificar o operador (isso é papel do
    /// <see cref="CentralizeEntity.AgentLogin"/>).
    /// </summary>
    public string? AuthorName { get; init; }

    /// <summary>Conteúdo da mensagem.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Arquivo enviado <b>nesta</b> mensagem, quando houver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fica na mensagem, e não apenas em
    /// <see cref="CentralizeEntity.Attachments"/>, porque o único bilhete que
    /// sabe carregar arquivo — o <c>.CHW</c> — guarda o caminho <b>por
    /// mensagem</b>. Uma lista solta no atendimento não diz em que ponto da
    /// conversa o arquivo apareceu, e todo documento acabaria empilhado no fim,
    /// fora do contexto em que foi enviado.
    /// </para>
    /// <para>
    /// Mensagem com arquivo pode vir sem <see cref="Text"/> — mandar só o
    /// documento é comum. Sem texto <b>e</b> sem arquivo, aí sim não há o que
    /// mostrar, e o item é recusado.
    /// </para>
    /// </remarks>
    public MediaAttachment? Attachment { get; init; }
}

/// <summary>Autor de uma mensagem de chat.</summary>
public enum ChatAuthor
{
    /// <summary>Não informado pela origem.</summary>
    Unknown = 0,

    /// <summary>Operador da empresa.</summary>
    Agent = 1,

    /// <summary>Cliente/contato.</summary>
    Contact = 2,

    /// <summary>Mensagem automática do sistema (bot, aviso de fila).</summary>
    System = 3
}
