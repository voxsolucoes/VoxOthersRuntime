namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// A conversa como ela viaja dentro do bilhete <c>.CHT</c>, no campo
/// <c>Json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Os nomes das propriedades são o contrato.</b> Do outro lado, o Vox
/// desserializa este JSON na classe <c>TransChat</c> do sistema atual. Renomear
/// qualquer campo aqui — inclusive só trocar a caixa das letras — faz o campo
/// chegar nulo lá, sem erro nenhum: o atendimento é importado e a conversa
/// aparece vazia. Por isso os nomes fogem da convenção do projeto e imitam os da
/// classe original, <c>UniqueIDOthers</c> e <c>OnlyANI</c> incluídos.
/// </para>
/// <para>
/// <b>Só o que o sistema atual preenche.</b> A <c>TransChat</c> tem dezenas de
/// campos <c>RE_*</c> específicos de cliente que o conector de referência
/// (<c>WSNetChat</c>) não usa. Campo ausente no JSON vira o valor padrão do
/// outro lado, então omitir é seguro; preencher "porque temos o dado" mudaria o
/// que o importador vê hoje.
/// </para>
/// </remarks>
public sealed record ChatTranscript
{
    /// <summary>Identificador do atendimento na origem.</summary>
    public required string UniqueIDOthers { get; init; }

    /// <summary>Quem enviou o lote.</summary>
    public required string Source { get; init; }

    /// <summary>Início do atendimento, na hora local do servidor.</summary>
    public required DateTime StartChat { get; init; }

    /// <summary>Nome do operador.</summary>
    public required string UserAgent { get; init; }

    /// <summary>Nome do contato, quando a origem informa.</summary>
    public string? Client { get; init; }

    /// <summary>Telefone/identificador do contato.</summary>
    public string? OnlyANI { get; init; }

    /// <summary>Duração do atendimento em segundos.</summary>
    public required long ChatDuration { get; init; }

    /// <summary>As mensagens, em ordem cronológica.</summary>
    public required IReadOnlyList<ChatTranscriptItem> ChatItems { get; init; }
}

/// <summary>Uma mensagem dentro do bilhete de chat.</summary>
public sealed record ChatTranscriptItem
{
    /// <summary>
    /// Quantos segundos depois do início do atendimento a mensagem foi enviada.
    /// </summary>
    /// <remarks>
    /// É por este número que o Vox posiciona a mensagem na conversa — não há
    /// data por mensagem no que o sistema atual grava.
    /// </remarks>
    public required long StartSeconds { get; init; }

    /// <summary>Texto da mensagem.</summary>
    public required string Text { get; init; }

    /// <summary>
    /// Lado da conversa: <c>0</c> é o operador, <c>1</c> é o outro lado.
    /// </summary>
    /// <remarks>
    /// São só dois valores porque o formato do sistema atual só tem dois lados.
    /// Mensagem de sistema (bot, aviso de fila) entra como <c>1</c>: ela não foi
    /// escrita pelo operador, e marcá-la como se fosse atribuiria ao atendente
    /// uma fala que não é dele — o que é pior do que agrupá-la com o contato.
    /// </remarks>
    public required int Participant { get; init; }
}
