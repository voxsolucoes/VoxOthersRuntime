using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Grava o bilhete <c>.CHW</c> do atendimento de texto <b>com anexo</b>.
/// </summary>
/// <remarks>
/// <para>
/// Terceiro formato de saída, e o único que sabe carregar arquivo. Reaproveita
/// a mesma colocação de mídia, o mesmo nome de bilhete e a mesma publicação em
/// duas etapas dos outros dois; o que muda é que antes do bilhete os anexos vão
/// para a árvore de gravação, e que o bilhete é gravado em XML.
/// </para>
/// <para>
/// <b>Ordem importa.</b> Os anexos são colocados antes de o bilhete existir,
/// pelo mesmo motivo da gravação de voz: bilhete que chega primeiro faz o Vox
/// procurar um arquivo que ainda está a caminho.
/// </para>
/// </remarks>
public sealed class WhatsAppChatImportSink : IImportSink
{
    /// <summary>Extensão do bilhete de chat com anexo.</summary>
    public const string Extensao = "CHW";

    private readonly WhatsAppChatTicketFactory _factory;
    private readonly IMediaPlacement _placement;
    private readonly IAttachmentPlacement _attachments;
    private readonly TicketPublisher _publisher;
    private readonly IOptionsMonitor<GrfOptions> _options;
    private readonly ILogger<WhatsAppChatImportSink> _logger;

    public WhatsAppChatImportSink(
        WhatsAppChatTicketFactory factory,
        IMediaPlacement placement,
        IAttachmentPlacement attachments,
        TicketPublisher publisher,
        IOptionsMonitor<GrfOptions> options,
        ILogger<WhatsAppChatImportSink> logger)
    {
        _factory = factory;
        _placement = placement;
        _attachments = attachments;
        _publisher = publisher;
        _options = options;
        _logger = logger;
    }

    public async Task<string> ProcessAsync(ImportedItemContext context, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var entity = context.Entity;

        var midia = await _placement.PlaceAsync(entity, context.ChannelNumber, cancellationToken);

        var colocadas = new List<PlacedChatMessage>();
        var quantosAnexos = 0;

        foreach (var pendente in WhatsAppChatTicketFactory.Organizar(entity))
        {
            AttachmentPlacementResult? anexo = null;

            if (pendente.Message.Attachment is not null)
            {
                anexo = await _attachments.PlaceAsync(
                    pendente.Message.Attachment,
                    pendente.Message.SentAt,
                    context.ChannelNumber,
                    pendente.Id,
                    cancellationToken);

                quantosAnexos++;
            }

            colocadas.Add(new PlacedChatMessage
            {
                Id = pendente.Id,
                Message = pendente.Message,
                Attachment = anexo
            });
        }

        var tkt = _factory.Create(new WhatsAppChatTicketInput
        {
            Entity = entity,
            ServerName = options.ServerName,
            ChannelNumber = context.ChannelNumber,
            CodLogin = context.CodLogin,
            Source = context.Source,
            Messages = colocadas
        });

        var destino = _publisher.Publish(
            tkt, Extensao, options.WorkPath, options.RegisterPath,
            midia.BaseName, FormatoDoBilhete.Xml, cancellationToken);

        _logger.Here().Info(
            "Bilhete de chat com anexo gravado: {UniqueId} ({Mensagens} mensagens, {Anexos} anexos) -> {Caminho}",
            entity.UniqueId, colocadas.Count, quantosAnexos, destino);

        return destino;
    }
}
