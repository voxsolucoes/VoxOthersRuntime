using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Grava o bilhete <c>.CHT</c> do atendimento de texto.
/// </summary>
/// <remarks>
/// Espelha o <see cref="GrfImportSink"/> e reaproveita tudo o que dá: a mesma
/// colocação de mídia, o mesmo nome de arquivo, a mesma pasta de registro, a
/// mesma publicação em duas etapas. O que muda é só o bilhete — outro tipo e
/// outra extensão. O REGBD lê os dois da mesma pasta e se vira pelo conteúdo.
/// </remarks>
public sealed class ChatImportSink : IImportSink
{
    /// <summary>Extensão do bilhete de atendimento de texto.</summary>
    public const string Extensao = "CHT";

    private readonly ChatTicketFactory _factory;
    private readonly IMediaPlacement _placement;
    private readonly TicketPublisher _publisher;
    private readonly IOptionsMonitor<GrfOptions> _options;
    private readonly ILogger<ChatImportSink> _logger;

    public ChatImportSink(
        ChatTicketFactory factory,
        IMediaPlacement placement,
        TicketPublisher publisher,
        IOptionsMonitor<GrfOptions> options,
        ILogger<ChatImportSink> logger)
    {
        _factory = factory;
        _placement = placement;
        _publisher = publisher;
        _options = options;
        _logger = logger;
    }

    public async Task<string> ProcessAsync(ImportedItemContext context, CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var entity = context.Entity;

        // Chat quase sempre não tem arquivo, e aí isto só calcula o nome. Quando
        // tem — a origem exportou a conversa em HTML, por exemplo —, o arquivo
        // vai para a árvore de gravação antes do bilhete, pelo mesmo motivo da
        // voz: bilhete que chega primeiro faz o Vox procurar o que ainda está a
        // caminho.
        var midia = await _placement.PlaceAsync(entity, context.ChannelNumber, cancellationToken);

        var input = new ChatTicketInput
        {
            Entity = entity,
            ServerName = options.ServerName,
            ChannelNumber = context.ChannelNumber,
            OperatorName = context.UserName,
            CodLogin = context.CodLogin,
            UserCodeUsuario = context.UserCodeUsuario,
            Source = context.Source
        };

        var tkt = _factory.Create(input);

        var destino = _publisher.Publish(
            tkt, Extensao, options.WorkPath, options.RegisterPath,
            midia.BaseName, FormatoDoBilhete.Texto, cancellationToken);

        _logger.Here().Info(
            "Bilhete de chat gravado: {UniqueId} ({Mensagens} mensagens) -> {Caminho}",
            entity.UniqueId, entity.Messages.Count, destino);

        return destino;
    }
}
