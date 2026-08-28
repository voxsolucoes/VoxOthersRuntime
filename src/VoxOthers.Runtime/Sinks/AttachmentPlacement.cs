using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// O anexo não pôde ser levado para a árvore de gravação.
/// </summary>
/// <remarks>
/// Herda de <see cref="ItemRejectedException"/> pelo mesmo motivo de
/// <see cref="MediaUnavailableException"/>: é problema do dado, e o item vai
/// para a quarentena marcado como algo a corrigir na origem.
/// </remarks>
public sealed class AttachmentUnavailableException(string message, Exception? inner = null)
    : ItemRejectedException(message, inner);

/// <summary>Onde o anexo ficou, do ponto de vista do bilhete.</summary>
public sealed record AttachmentPlacementResult
{
    /// <summary>
    /// Caminho relativo à raiz de gravação, incluindo o nome do arquivo. É o que
    /// sai em <c>fileInfo.media_path</c>.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>Nome com que o arquivo ficou gravado.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Vocabulário do bilhete: <c>image</c>, <c>video</c>, <c>audio</c> ou
    /// <c>document</c>.
    /// </summary>
    public required string MessageType { get; init; }

    /// <summary>Tipo do conteúdo, quando a origem informou.</summary>
    public string? MimeType { get; init; }
}

public interface IAttachmentPlacement
{
    Task<AttachmentPlacementResult> PlaceAsync(
        MediaAttachment anexo,
        DateTimeOffset quando,
        int channelNumber,
        string messageId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Coloca o anexo do chat na árvore de gravação, na mesma convenção que o
/// sistema atual usa para mídia de WhatsApp.
/// </summary>
/// <remarks>
/// <para>
/// A convenção não foi escolhida por nós — é a de
/// <c>VoxSoftphoneChatController.DownloadToGrav</c>:
/// </para>
/// <code>
/// {ano}\{MM}\{dd}\{canal:0000}\attachment_wpp\{canal}_{tipo}_{idMensagem}{ext}
/// </code>
/// <para>
/// O nome <c>attachment_wpp</c> ficou, mesmo para anexo que não veio de
/// WhatsApp. Inventar uma pasta nova espalharia anexo por dois lugares na mesma
/// árvore sem nenhum ganho, e quem der suporte procuraria no lugar de sempre.
/// </para>
/// <para>
/// O que vai no bilhete é o caminho <b>relativo</b>. O absoluto depende de onde
/// a raiz de gravação está montada, que é diferente em cada máquina — bilhete
/// com caminho absoluto funciona no servidor que o gerou e em nenhum outro.
/// </para>
/// </remarks>
public sealed class AttachmentPlacement : IAttachmentPlacement
{
    private const string PastaDeAnexos = "attachment_wpp";

    private readonly IOptionsMonitor<GrfOptions> _options;
    private readonly ILogger<AttachmentPlacement> _logger;

    public AttachmentPlacement(IOptionsMonitor<GrfOptions> options, ILogger<AttachmentPlacement> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<AttachmentPlacementResult> PlaceAsync(
        MediaAttachment anexo,
        DateTimeOffset quando,
        int channelNumber,
        string messageId,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;

        ConferirOrigem(anexo.Path);

        var tipo = TipoDaMensagem(anexo.ContentType);
        var nome = $"{channelNumber}_{tipo}_{messageId}{Extensao(anexo)}";

        var pastaRelativa = Path.Combine(
            MediaPlacement.CaminhoRelativo(quando, channelNumber), PastaDeAnexos);

        var pastaDestino = Path.Combine(options.RecordingRoot, pastaRelativa);
        var destinoFinal = Path.Combine(pastaDestino, nome);

        Directory.CreateDirectory(pastaDestino);

        try
        {
            await GravFileCopier.CopyAsync(anexo.Path, destinoFinal, _logger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AttachmentUnavailableException(
                $"Falha ao colocar o anexo '{anexo.Path}' em '{destinoFinal}': {ex.Message}", ex);
        }

        return new AttachmentPlacementResult
        {
            RelativePath = Path.Combine(pastaRelativa, nome),
            FileName = nome,
            MessageType = tipo,
            MimeType = anexo.ContentType
        };
    }

    /// <summary>
    /// Recusa o anexo antes de tentar copiá-lo, com o motivo exato.
    /// </summary>
    /// <remarks>
    /// O <c>ContentValidation</c> já confere que o arquivo existe, bem antes,
    /// para que o item não chegue a mexer na base do Vox. Aqui se confere de
    /// novo porque entre uma coisa e outra o arquivo pode ter sumido, e porque
    /// a conferência precisa estar colada na cópia: anexo vazio ou ainda sendo
    /// escrito produziria um arquivo truncado na grav com o bilhete apontando
    /// para ele, o que ninguém notaria.
    /// </remarks>
    private static void ConferirOrigem(string origem)
    {
        if (string.IsNullOrWhiteSpace(origem))
        {
            throw new AttachmentUnavailableException("Anexo veio sem caminho de arquivo.");
        }

        if (!File.Exists(origem))
        {
            throw new AttachmentUnavailableException($"Anexo não encontrado: '{origem}'.");
        }

        if (new FileInfo(origem).Length == 0)
        {
            throw new AttachmentUnavailableException($"Anexo está vazio: '{origem}'.");
        }

        try
        {
            using var _ = new FileStream(origem, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException ex)
        {
            throw new AttachmentUnavailableException(
                $"Anexo '{origem}' está em uso — provavelmente ainda sendo gravado.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AttachmentUnavailableException(
                $"Sem permissão de leitura no anexo '{origem}'.", ex);
        }
    }

    /// <summary>
    /// Vocabulário de tipo de mensagem do bilhete, deduzido do tipo do conteúdo.
    /// </summary>
    /// <remarks>
    /// <c>document</c> é o padrão porque é o tipo que o Vox trata como "arquivo
    /// para baixar". Chutar <c>image</c> num arquivo que não é imagem faria a
    /// tela tentar exibi-lo e mostrar um quadro quebrado.
    /// </remarks>
    internal static string TipoDaMensagem(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return "document";
        }

        return contentType.Split('/')[0].Trim().ToLowerInvariant() switch
        {
            "image" => "image",
            "video" => "video",
            "audio" => "audio",
            _ => "document"
        };
    }

    /// <summary>
    /// Extensão do arquivo, na ordem em que a informação é confiável.
    /// </summary>
    /// <remarks>
    /// O nome declarado pela origem vem primeiro porque é o que o usuário
    /// enviou; o caminho no disco costuma ser um nome temporário do Builder. O
    /// tipo do conteúdo é o último recurso — sem extensão nenhuma, o Windows
    /// não sabe com o que abrir o arquivo baixado.
    /// </remarks>
    internal static string Extensao(MediaAttachment anexo)
    {
        var doNome = Path.GetExtension(anexo.FileName ?? string.Empty);
        if (!string.IsNullOrEmpty(doNome)) return doNome;

        var doCaminho = Path.GetExtension(anexo.Path);
        if (!string.IsNullOrEmpty(doCaminho)) return doCaminho;

        return DoContentType(anexo.ContentType);
    }

    private static string DoContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return string.Empty;

        var partes = contentType.Split('/');
        if (partes.Length < 2) return string.Empty;

        var sub = partes[1].Split(';')[0].Trim().ToLowerInvariant();

        return sub switch
        {
            "" => string.Empty,
            "jpeg" => ".jpg",
            "plain" => ".txt",
            _ => "." + sub
        };
    }
}
