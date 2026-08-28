using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// A mídia não está onde o Vox espera, e não adianta insistir.
/// </summary>
/// <remarks>
/// Herda de <see cref="ItemRejectedException"/> porque é problema do dado, não
/// do ambiente: é isso que faz o item ir para a quarentena marcado como algo a
/// corrigir na origem, e não como falha passageira. A mensagem é escrita para
/// ser lida por quem for investigar — não por outro programa.
/// </remarks>
public sealed class MediaUnavailableException(string message, Exception? inner = null)
    : ItemRejectedException(message, inner);

/// <summary>Onde a mídia ficou, do ponto de vista do bilhete.</summary>
public sealed record MediaPlacementResult
{
    /// <summary>Caminho relativo à raiz de gravação. Sai no campo <c>CA</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Nome do arquivo. Sai no campo <c>NO</c>.</summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Nome que o bilhete deve levar, sem extensão.
    /// </summary>
    /// <remarks>
    /// Calculado aqui, e não por quem consome, porque só aqui se sabe se houve
    /// arquivo. Sem mídia o nome já vem sem extensão, e passá-lo por
    /// <c>GetFileNameWithoutExtension</c> cortaria tudo a partir do primeiro
    /// ponto do identificador — atendimento de texto com id <c>conv.123</c>
    /// geraria bilhete chamado <c>…_conv</c>, colidindo com o de
    /// <c>conv.456</c>. Em chat isso é o caso comum, não a exceção.
    /// </remarks>
    public required string BaseName { get; init; }
}

/// <summary>
/// Confere a mídia do atendimento e a coloca na árvore de gravação do Vox.
/// </summary>
public interface IMediaPlacement
{
    Task<MediaPlacementResult> PlaceAsync(
        CentralizeEntity entity,
        int channelNumber,
        CancellationToken cancellationToken);
}

public sealed class MediaPlacement : IMediaPlacement
{
    private readonly IOptionsMonitor<GrfOptions> _options;
    private readonly ILogger<MediaPlacement> _logger;

    public MediaPlacement(IOptionsMonitor<GrfOptions> options, ILogger<MediaPlacement> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<MediaPlacementResult> PlaceAsync(
        CentralizeEntity entity,
        int channelNumber,
        CancellationToken cancellationToken)
    {
        var options = _options.CurrentValue;
        var relativo = CaminhoRelativo(entity.StartedAt, channelNumber);

        // Atendimento de texto sem arquivo não tem mídia para colocar. O
        // bilhete continua sendo gerado, com um nome montado no mesmo padrão
        // para que ele siga identificável.
        if (string.IsNullOrWhiteSpace(entity.MediaPath))
        {
            var semArquivo = NomePadrao(channelNumber, entity);

            return new MediaPlacementResult
            {
                RelativePath = relativo,
                FileName = semArquivo,
                BaseName = semArquivo
            };
        }

        var origem = entity.MediaPath;
        var nomeOriginal = Path.GetFileName(origem.Replace('\\', '/'));

        if (string.IsNullOrWhiteSpace(nomeOriginal))
        {
            throw new MediaUnavailableException(
                $"MediaPath '{origem}' não termina em nome de arquivo.");
        }

        ConferirOrigem(origem);

        var nome = NomePadrao(channelNumber, entity) + Path.GetExtension(nomeOriginal);

        var pastaDestino = Path.Combine(options.RecordingRoot, relativo);
        var destinoFinal = Path.Combine(pastaDestino, nome);

        Directory.CreateDirectory(pastaDestino);

        try
        {
            await GravFileCopier.CopyAsync(origem, destinoFinal, _logger, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new MediaUnavailableException(
                $"Falha ao colocar a mídia '{origem}' em '{destinoFinal}': {ex.Message}", ex);
        }

        return new MediaPlacementResult
        {
            RelativePath = relativo,
            FileName = nome,
            BaseName = Path.GetFileNameWithoutExtension(nome)
        };
    }

    /// <summary>
    /// Nome padrão da gravação: <c>C&lt;canal&gt;-&lt;yyyyMMddHHmmss&gt;_&lt;callid&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// É o mesmo padrão dos bilhetes de produção — canal com ao menos quatro
    /// dígitos, data de início do atendimento junta, e o callid do campo livre
    /// <c>CALLID</c> quando a origem o envia. Sem callid o nome fica só com
    /// canal e data, que é o mínimo para o Vox localizar o áudio.
    /// </para>
    /// <para>
    /// A busca pelo <c>CALLID</c> é sem sensibilidade a caixa — o Builder pode
    /// mandar <c>CALLID</c>, <c>CallId</c> ou <c>callid</c>.
    /// </para>
    /// </remarks>
    private static string NomePadrao(int canal, CentralizeEntity entity)
    {
        var callId = entity.Extensions?
            .FirstOrDefault(kv => string.Equals(kv.Key, "CALLID", StringComparison.OrdinalIgnoreCase))
            .Value;

        var nome = $"C{canal:0000}-{entity.StartedAt:yyyyMMddHHmmss}";
        if (!string.IsNullOrWhiteSpace(callId))
        {
            nome += "_" + callId;
        }
        return nome;
    }

    /// <summary>
    /// Recusa a mídia antes de tentar movê-la, com o motivo exato.
    /// </summary>
    /// <remarks>
    /// Três checagens distintas porque as três causas pedem ações diferentes de
    /// quem for resolver: caminho errado no Builder, permissão faltando, ou
    /// arquivo ainda sendo escrito. Uma mensagem genérica de "falha na mídia"
    /// obrigaria a investigar as três.
    /// </remarks>
    private static void ConferirOrigem(string origem)
    {
        if (!File.Exists(origem))
        {
            throw new MediaUnavailableException($"Arquivo de mídia não encontrado: '{origem}'.");
        }

        if (new FileInfo(origem).Length == 0)
        {
            throw new MediaUnavailableException($"Arquivo de mídia está vazio: '{origem}'.");
        }

        try
        {
            // FileShare.Read e não ReadWrite: se alguém ainda está gravando
            // neste arquivo, isto falha — que é o que se quer. Copiar mídia pela
            // metade produziria uma gravação truncada que ninguém perceberia,
            // porque o bilhete sairia normal.
            using var _ = new FileStream(origem, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (IOException ex)
        {
            throw new MediaUnavailableException(
                $"Arquivo de mídia '{origem}' está em uso — provavelmente ainda sendo gravado.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new MediaUnavailableException(
                $"Sem permissão de leitura no arquivo de mídia '{origem}'.", ex);
        }
    }

    /// <summary>
    /// Caminho da gravação relativo à raiz: <c>aaaa\MM\dd\canal</c>.
    /// </summary>
    /// <remarks>
    /// O canal vai com <b>no mínimo quatro dígitos</b>, completado com zeros à
    /// esquerda — canal 350 vira <c>0350</c>, canal 10381 continua
    /// <c>10381</c>. Não é enfeite: é o nome da pasta que o Vox usa para achar
    /// a gravação, conferido contra a árvore de um ambiente real. Sem os zeros,
    /// todo canal abaixo de mil geraria bilhete apontando para pasta
    /// inexistente, e a gravação não seria encontrada.
    /// </remarks>
    internal static string CaminhoRelativo(DateTimeOffset inicio, int canal)
        => $@"{inicio:yyyy}\{inicio:MM}\{inicio:dd}\{canal:0000}";
}
