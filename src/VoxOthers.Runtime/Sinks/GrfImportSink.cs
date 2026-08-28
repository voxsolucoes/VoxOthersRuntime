using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Grava o bilhete <c>.GRF</c> na pasta de registro do Vox.
/// </summary>
/// <remarks>
/// <para>
/// É a fronteira do projeto: daqui em diante quem trabalha é o serviço de
/// importação do Vox, que já existe e não foi tocado.
/// </para>
/// <para>
/// A gravação é em duas etapas — escreve na pasta de trabalho, move para a de
/// registro. Ver <see cref="TicketPublisher"/> para o porquê.
/// </para>
/// </remarks>
public sealed class GrfImportSink : IImportSink
{
    /// <summary>Extensão do bilhete de gravação de voz.</summary>
    public const string Extensao = "GRF";

    private readonly GrfTicketFactory _factory;
    private readonly IMediaPlacement _placement;
    private readonly TicketPublisher _publisher;
    private readonly IOptionsMonitor<GrfOptions> _options;
    private readonly ILogger<GrfImportSink> _logger;

    public GrfImportSink(
        GrfTicketFactory factory,
        IMediaPlacement placement,
        TicketPublisher publisher,
        IOptionsMonitor<GrfOptions> options,
        ILogger<GrfImportSink> logger)
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

        // A mídia vai para o lugar ANTES do bilhete ser gravado, e a ordem é o
        // que importa aqui. O importador do Vox reage ao bilhete: se ele
        // aparecer primeiro, o Vox procura uma gravação que ainda está a
        // caminho e registra o atendimento sem áudio. Na ordem inversa, o pior
        // caso é mídia colocada e bilhete não gerado — o item é reprocessado e
        // a mídia já estar lá é tratado como reprocessamento normal.
        var midia = await _placement.PlaceAsync(entity, context.ChannelNumber, cancellationToken);

        var input = new GrfTicketInput
        {
            Entity = entity,
            ServerName = options.ServerName,
            ChannelNumber = context.ChannelNumber,
            RelativePath = midia.RelativePath,
            MediaFileName = midia.FileName,
            OperatorName = context.UserName,
            CodLogin = context.CodLogin,
            Source = context.Source
        };

        var tkt = _factory.Create(input);

        var destino = _publisher.Publish(
            tkt, Extensao, options.WorkPath, options.RegisterPath,
            midia.BaseName, FormatoDoBilhete.Texto, cancellationToken);

        _logger.Here().Info(
            "Bilhete gravado: {UniqueId} -> {Caminho}",
            entity.UniqueId, destino);

        return destino;
    }
}
