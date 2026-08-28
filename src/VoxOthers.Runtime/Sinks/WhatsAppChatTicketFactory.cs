using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vox.RegBDLib;
using Vox.RegBDLib.chatWpp;
using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Mensagem já ordenada e com identificador estável, antes de o anexo ser
/// colocado na árvore de gravação.
/// </summary>
public sealed record PendingChatMessage
{
    public required string Id { get; init; }
    public required ChatMessage Message { get; init; }
}

/// <summary>
/// Mensagem pronta para virar item do bilhete: já ordenada e com o anexo, se
/// houver, já na árvore de gravação.
/// </summary>
public sealed record PlacedChatMessage
{
    public required string Id { get; init; }
    public required ChatMessage Message { get; init; }
    public AttachmentPlacementResult? Attachment { get; init; }
}

/// <summary>
/// Traduz um atendimento de texto <b>com anexo</b> no bilhete <c>.CHW</c> da
/// <c>RegBDLib</c>.
/// </summary>
/// <remarks>
/// <para>
/// O <c>.CHT</c> é mensagem pura: nem ele, nem sua classe base, nem a
/// <c>TransChat</c> que o lê do outro lado têm campo de arquivo. Quem carrega
/// arquivo é o <c>CRegBD261ChatWppTkt</c>, gravado como <c>.CHW</c>, onde cada
/// mensagem tem um <c>fileInfo</c> com o caminho relativo na grav. Ver AD-20.
/// </para>
/// <para>
/// <b>Os itens da conversa usam a classe da própria biblioteca</b>
/// (<see cref="ChatTicketEntity"/>) em vez de uma cópia nossa. É a defesa mais
/// forte possível contra o risco que a AD-19 já apontava: o JSON é
/// desserializado do outro lado nessa mesma classe, e um nome de propriedade
/// escrito diferente — nem que seja só na caixa das letras — chega nulo, sem
/// erro nenhum. Usando a classe original, não há como divergir.
/// </para>
/// </remarks>
public sealed class WhatsAppChatTicketFactory
{
    /// <summary>
    /// Formato do JSON da conversa — o mesmo do <c>.CHT</c>.
    /// </summary>
    /// <remarks>
    /// Codificador relaxado pelo mesmo motivo explicado em
    /// <see cref="ChatTicketFactory"/>: o sistema atual escreve com Newtonsoft,
    /// que não escapa acento, e este bilhete é lido pelo mesmo importador.
    /// </remarks>
    private static readonly JsonSerializerOptions Formato = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public CRegBD261ChatWppTkt Create(WhatsAppChatTicketInput input)
    {
        var e = input.Entity;

        return new CRegBD261ChatWppTkt
        {
            Server = input.ServerName,
            Channel = input.ChannelNumber,

            IdChat = e.UniqueId,
            Phone = SomenteDigitos(e.Ani),
            DateChat = e.StartedAt.LocalDateTime,
            SourceChat = input.Source,
            CodLogin = input.CodLogin,

            ChatList = JsonSerializer.Serialize(Itens(input), Formato)
        };
    }

    /// <summary>
    /// Monta os itens que vão no campo <c>ChatList</c>.
    /// </summary>
    internal static List<ChatTicketEntity> Itens(WhatsAppChatTicketInput input)
    {
        var e = input.Entity;
        var telefone = SomenteDigitos(e.Ani);

        return input.Messages.Select(p => new ChatTicketEntity
        {
            chatIdentificationKey = e.UniqueId,
            idMessage = p.Id,
            phone = telefone,
            from = telefone,
            timeStamp = SemFracaoDeSegundo(p.Message.SentAt),

            messageType = p.Attachment?.MessageType ?? "text_message",
            text = p.Message.Text ?? string.Empty,

            // O operador é quem "envia" do ponto de vista do Vox, igual ao
            // Participant 0 do .CHT — os dois formatos precisam concordar sobre
            // de que lado da tela a mensagem aparece.
            isOutgoing = p.Message.Author == ChatAuthor.Agent,

            pushName = p.Message.AuthorName,
            nameChat = e.ContactName,

            idServer = e.ServerId,
            idOperation = e.OperationId,
            source = input.Source,

            fileInfo = p.Attachment is null ? null : new FileInfoTicket
            {
                media_path = p.Attachment.RelativePath,
                mime_type = p.Attachment.MimeType,

                // No bilhete do sistema atual a legenda é o texto que acompanha
                // o arquivo. Aqui é o próprio texto da mensagem, que é o mesmo
                // papel — mensagem só com arquivo vem com texto vazio.
                caption = p.Message.Text ?? string.Empty
            }
        }).ToList();
    }

    /// <summary>
    /// Ordena a conversa e dá a cada mensagem um identificador estável.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ordenação é a mesma do <c>.CHT</c>, e estável pelo mesmo motivo:
    /// mensagem fora de ordem é corrigida, mensagens com o mesmo instante
    /// mantêm a ordem em que chegaram.
    /// </para>
    /// <para>
    /// <b>Anexo declarado no atendimento, e não numa mensagem</b>, vira uma
    /// mensagem própria no fim da conversa. Não é o ideal — perde-se o ponto em
    /// que o arquivo apareceu —, mas é o único jeito de não perder o arquivo, e
    /// perder arquivo em silêncio é o pior desfecho possível. Quem quiser o
    /// anexo no lugar certo o declara em
    /// <see cref="ChatMessage.Attachment"/>.
    /// </para>
    /// </remarks>
    internal static List<PendingChatMessage> Organizar(CentralizeEntity e)
    {
        var ordenadas = e.Messages.OrderBy(m => m.SentAt).ToList();

        // Fim do atendimento: é onde os anexos soltos entram, para que fiquem
        // depois de tudo o que foi realmente conversado.
        var fim = e.StartedAt.AddSeconds(e.DurationSeconds);

        ordenadas.AddRange(e.Attachments.Select(anexo => new ChatMessage
        {
            SentAt = fim,
            Author = ChatAuthor.Unknown,
            Text = string.Empty,
            Attachment = anexo
        }));

        return ordenadas
            .Select((m, i) => new PendingChatMessage
            {
                Id = $"{e.UniqueId}-{i.ToString("000", CultureInfo.InvariantCulture)}",
                Message = m
            })
            .ToList();
    }

    /// <summary>
    /// Corta a fração de segundo do horário da mensagem.
    /// </summary>
    /// <remarks>
    /// Não é capricho: o comentário do sistema atual
    /// (<c>VoxSoftphoneChatController.ToLocalTimestamp</c>) registra que a
    /// fração muda o formato serializado e que alguns leitores do portal não
    /// conseguem interpretá-lo, deixando de exibir a mensagem. Sem erro nenhum
    /// — a mensagem simplesmente não aparece.
    /// </remarks>
    internal static DateTime SemFracaoDeSegundo(DateTimeOffset instante)
    {
        var local = instante.LocalDateTime;
        return local.AddTicks(-(local.Ticks % TimeSpan.TicksPerSecond));
    }

    /// <summary>
    /// O bilhete guarda telefone só com dígitos.
    /// </summary>
    /// <remarks>
    /// A origem manda o número como bem entende — com <c>+</c>, parênteses,
    /// traço. O campo é usado para casar a conversa com o contato, e
    /// <c>(11) 4002-8922</c> não casa com <c>1140028922</c>.
    /// </remarks>
    internal static string SomenteDigitos(string? valor)
        => string.IsNullOrEmpty(valor)
            ? string.Empty
            : new string(valor.Where(char.IsAsciiDigit).ToArray());
}

/// <summary>
/// O que o bilhete <c>.CHW</c> precisa e que não vem do contrato.
/// </summary>
public sealed record WhatsAppChatTicketInput
{
    public required CentralizeEntity Entity { get; init; }

    /// <summary>Nome do servidor Vox.</summary>
    public required string ServerName { get; init; }

    /// <summary>Canal alocado para o atendimento.</summary>
    public required int ChannelNumber { get; init; }

    /// <summary>Código do login resolvido na base.</summary>
    public required string CodLogin { get; init; }

    /// <summary>Origem do lote.</summary>
    public required string Source { get; init; }

    /// <summary>Conversa ordenada, com os anexos já na árvore de gravação.</summary>
    public required IReadOnlyList<PlacedChatMessage> Messages { get; init; }
}
