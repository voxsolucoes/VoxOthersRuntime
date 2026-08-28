namespace VoxOthers.Contracts;

/// <summary>
/// Arquivo adicional ligado ao atendimento (documento enviado no chat,
/// screenshot, transcrição gerada pela origem).
/// </summary>
public sealed class MediaAttachment
{
    /// <summary>
    /// Caminho completo do arquivo, acessível pelo Runtime. O Builder é
    /// responsável por deixar o arquivo em um local que o Runtime enxergue.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Nome com que o anexo deve ser guardado. Vazio significa usar o nome
    /// original do arquivo.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>Tipo do conteúdo, quando a origem informa. Ex.: application/pdf.</summary>
    public string? ContentType { get; init; }
}
