using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// Cuida do arquivo enquanto ele caminha pelas pastas: entrada, trabalho,
/// concluído e quarentena.
/// </summary>
/// <remarks>
/// Está separado da varredura de propósito. Movimentação de arquivo é onde
/// mora a maior parte dos casos de canto — nome repetido, pasta que não existe,
/// arquivo ainda aberto — e concentrá-la aqui deixa a varredura legível e esta
/// parte testável isoladamente.
/// </remarks>
public sealed class IngestionFileStore(
    IOptionsMonitor<IngestionOptions> options,
    ILogger<IngestionFileStore> logger)
{
    private FolderIngestionOptions Folder => options.CurrentValue.Folder;

    /// <summary>
    /// Cria as pastas de destino que faltarem.
    /// </summary>
    /// <remarks>
    /// Só as de destino. As de entrada, não: elas são o combinado com o
    /// Builder, e criá-las sozinho esconderia um caminho digitado errado —
    /// o serviço ficaria vigiando uma pasta vazia que ninguém usa.
    /// </remarks>
    public void EnsureDestinationFolders()
    {
        foreach (var caminho in new[] { Folder.WorkingPath, Folder.ProcessedPath, Folder.QuarantinePath })
        {
            if (!string.IsNullOrWhiteSpace(caminho))
            {
                Directory.CreateDirectory(caminho);
            }
        }
    }

    /// <summary>
    /// Devolve para a entrada os arquivos que ficaram na pasta de trabalho.
    /// </summary>
    /// <remarks>
    /// Arquivo parado ali significa que o serviço caiu no meio do
    /// processamento. Sem esta devolução ele ficaria esquecido para sempre —
    /// e gravação perdida por queda de serviço é exatamente o que a Fase 5
    /// promete eliminar. Fazer isso no boot custa quase nada e fecha o buraco
    /// desde já.
    /// </remarks>
    public int RecoverAbandoned()
    {
        var entrada = Folder.Paths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));

        if (string.IsNullOrWhiteSpace(Folder.WorkingPath)
            || string.IsNullOrWhiteSpace(entrada)
            || !Directory.Exists(Folder.WorkingPath))
        {
            return 0;
        }

        var recuperados = 0;

        foreach (var arquivo in Directory.EnumerateFiles(Folder.WorkingPath))
        {
            var destino = CaminhoLivre(entrada, Path.GetFileName(arquivo));

            try
            {
                File.Move(arquivo, destino);
                recuperados++;

                logger.Here().Warn(
                    "Lote interrompido devolvido para a entrada: {Arquivo}. " +
                    "O serviço provavelmente foi encerrado no meio do processamento.",
                    destino);
            }
            catch (IOException ex)
            {
                logger.Here().Error(ex, "Não foi possível devolver {Arquivo} para a entrada.", arquivo);
            }
        }

        return recuperados;
    }

    /// <summary>
    /// Tira o arquivo da entrada e leva para a pasta de trabalho.
    /// </summary>
    /// <returns>O novo caminho, ou nulo se não deu para mover.</returns>
    public string? TryMoveToWorking(string inputPath)
    {
        var destino = CaminhoLivre(Folder.WorkingPath, Path.GetFileName(inputPath));

        try
        {
            File.Move(inputPath, destino);
            return destino;
        }
        catch (IOException ex)
        {
            // Concorrência normal: outra varredura pegou primeiro, ou o
            // arquivo ainda está sendo escrito. Não é erro — o próximo ciclo
            // tenta de novo.
            logger.Here().Debug(ex, "Arquivo {Arquivo} não pôde ser movido agora.", inputPath);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.Here().Error(ex, "Sem permissão para mover {Arquivo}.", inputPath);
            return null;
        }
    }

    /// <summary>Marca o arquivo como processado com sucesso.</summary>
    public void Complete(string workingPath)
        => Mover(workingPath, Folder.ProcessedPath, "concluído");

    /// <summary>
    /// Manda o arquivo para a quarentena, com o motivo em um arquivo ao lado.
    /// </summary>
    /// <remarks>
    /// O motivo vai para disco, e não só para o log. No sistema atual, item em
    /// quarentena só conta a própria história se alguém ainda tiver o log
    /// daquele dia — com o motivo ao lado, a pasta se explica sozinha meses
    /// depois.
    /// </remarks>
    public void Quarantine(string path, string reason)
    {
        var destino = Mover(path, Folder.QuarantinePath, "quarentena");

        if (destino is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(
                destino + ".motivo.txt",
                $"Data: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}{Environment.NewLine}" +
                $"Arquivo: {Path.GetFileName(destino)}{Environment.NewLine}" +
                $"Motivo: {reason}{Environment.NewLine}");
        }
        catch (IOException ex)
        {
            logger.Here().Error(ex, "Não foi possível gravar o motivo ao lado de {Arquivo}.", destino);
        }
    }

    private string? Mover(string origem, string pastaDestino, string rotulo)
    {
        if (!File.Exists(origem))
        {
            logger.Here().Warn("Arquivo {Arquivo} não existe mais ao tentar movê-lo para {Rotulo}.", origem, rotulo);
            return null;
        }

        var destino = CaminhoLivre(pastaDestino, Path.GetFileName(origem));

        try
        {
            Directory.CreateDirectory(pastaDestino);
            File.Move(origem, destino);
            return destino;
        }
        catch (IOException ex)
        {
            logger.Here().Error(ex, "Falha ao mover {Arquivo} para {Rotulo}.", origem, rotulo);
            return null;
        }
    }

    /// <summary>
    /// Encontra um nome livre na pasta de destino.
    /// </summary>
    /// <remarks>
    /// Dois lotes com o mesmo nome de arquivo são comuns — o Builder costuma
    /// nomear por data. Sobrescrever apagaria a evidência de um item que
    /// falhou; acrescentar um sufixo preserva os dois.
    /// </remarks>
    private static string CaminhoLivre(string pasta, string nomeArquivo)
    {
        var candidato = Path.Combine(pasta, nomeArquivo);

        if (!File.Exists(candidato))
        {
            return candidato;
        }

        var semExtensao = Path.GetFileNameWithoutExtension(nomeArquivo);
        var extensao = Path.GetExtension(nomeArquivo);

        for (var i = 1; i < 10_000; i++)
        {
            candidato = Path.Combine(pasta, $"{semExtensao} ({i}){extensao}");

            if (!File.Exists(candidato))
            {
                return candidato;
            }
        }

        return Path.Combine(pasta, $"{semExtensao} ({Guid.NewGuid():N}){extensao}");
    }
}
