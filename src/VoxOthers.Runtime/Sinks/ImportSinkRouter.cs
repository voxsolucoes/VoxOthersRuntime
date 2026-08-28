using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Manda o item para o bilhete certo: <c>.GRF</c> se for voz, <c>.CHW</c> se for
/// texto com anexo, <c>.CHT</c> se for texto puro.
/// </summary>
/// <remarks>
/// <para>
/// A escolha fica aqui, e não dentro do worker, porque o worker não deveria
/// conhecer tipo de bilhete — ele entrega ao <see cref="IImportSink"/> e pronto.
/// Também não fica dentro de um dos sinks: cada um continua sabendo fazer uma
/// coisa só, e um quarto tipo de saída entra acrescentando um sink e uma linha
/// aqui.
/// </para>
/// <para>
/// A prova de que a arquitetura da Fase 3 estava certa é o tamanho deste
/// arquivo: atendimento de texto, com anexo ou sem, entrou sem tocar em fila,
/// validação, deduplicação, cadastro ou quarentena.
/// </para>
/// </remarks>
public sealed class ImportSinkRouter : IImportSink
{
    private readonly GrfImportSink _voz;
    private readonly ChatImportSink _texto;
    private readonly WhatsAppChatImportSink _textoComAnexo;

    public ImportSinkRouter(
        GrfImportSink voz,
        ChatImportSink texto,
        WhatsAppChatImportSink textoComAnexo)
    {
        _voz = voz;
        _texto = texto;
        _textoComAnexo = textoComAnexo;
    }

    public Task<string> ProcessAsync(ImportedItemContext context, CancellationToken cancellationToken)
    {
        if (context.Entity.Kind != MediaKind.Chat)
        {
            return _voz.ProcessAsync(context, cancellationToken);
        }

        return TemAnexo(context.Entity)
            ? _textoComAnexo.ProcessAsync(context, cancellationToken)
            : _texto.ProcessAsync(context, cancellationToken);
    }

    /// <summary>
    /// O anexo é o que decide o formato do bilhete de texto.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Não é uma escolha de arquitetura, é o que a biblioteca permite: o
    /// <c>.CHT</c> é mensagem pura e não tem onde guardar caminho de arquivo, e
    /// o <c>.CHW</c> tem. Chat sem anexo continua saindo em <c>.CHT</c> porque é
    /// o formato que os 20 conectores de chat do sistema atual usam, e porque o
    /// <c>.CHW</c> <b>não carrega campos livres</b> — trocar de formato sem
    /// necessidade custaria o protocolo e os demais dados do cliente. Ver AD-20.
    /// </para>
    /// <para>
    /// Anexo pode vir declarado na mensagem ou no atendimento; os dois contam,
    /// senão um arquivo declarado do jeito "errado" sumiria em silêncio.
    /// </para>
    /// </remarks>
    internal static bool TemAnexo(CentralizeEntity entity)
        => entity.Attachments.Count > 0
           || entity.Messages.Any(m => m.Attachment is not null);
}
