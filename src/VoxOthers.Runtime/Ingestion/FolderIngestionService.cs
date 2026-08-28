using System.Text.Json;
using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Observability;

namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// Vigia as pastas de entrada e coloca na fila interna o que encontra.
/// </summary>
public sealed class FolderIngestionService(
    IOptionsMonitor<IngestionOptions> options,
    IngestionQueue queue,
    IngestionFileStore store,
    ILogger<FolderIngestionService> logger) : BackgroundService
{
    private FolderIngestionOptions Folder => options.CurrentValue.Folder;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Folder.Enabled)
        {
            logger.Here().Info("Entrada por pasta desligada na configuração.");
            return;
        }

        store.EnsureDestinationFolders();

        var recuperados = store.RecoverAbandoned();
        if (recuperados > 0)
        {
            logger.Here().Warn("{Quantidade} lote(s) interrompido(s) devolvido(s) para a entrada.", recuperados);
        }

        logger.Here().Info(
            "Vigiando {Quantidade} pasta(s) a cada {Intervalo}s: {Pastas}",
            Folder.Paths.Count,
            Folder.ScanIntervalSeconds,
            string.Join(", ", Folder.Paths));

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Folder.ScanIntervalSeconds));

        do
        {
            try
            {
                await ScanAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Uma varredura que falha não pode derrubar o vigia: o serviço
                // ficaria no ar, saudável, sem nunca mais olhar a pasta.
                logger.Here().Error(ex, "Falha na varredura das pastas. A próxima tentativa segue no horário.");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Percorre as pastas uma vez. Exposto para os testes.</summary>
    internal async Task<int> ScanAsync(CancellationToken cancellationToken)
    {
        var aceitos = 0;

        foreach (var pasta in Folder.Paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            if (!Directory.Exists(pasta))
            {
                logger.Here().Warn("Pasta de entrada inacessível: {Pasta}", pasta);
                continue;
            }

            foreach (var arquivo in Directory.EnumerateFiles(pasta, Folder.FilePattern))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!EstaLiberado(arquivo))
                {
                    logger.Here().Debug("Arquivo {Arquivo} ainda está sendo gravado; fica para a próxima varredura.", arquivo);
                    continue;
                }

                if (await ProcessarAsync(arquivo, cancellationToken).ConfigureAwait(false))
                {
                    aceitos++;
                }
            }
        }

        return aceitos;
    }

    private async Task<bool> ProcessarAsync(string arquivo, CancellationToken cancellationToken)
    {
        var emTrabalho = store.TryMoveToWorking(arquivo);

        if (emTrabalho is null)
        {
            return false;
        }

        var batchId = IngestionBatchId.New();

        using var escopo = logger.BeginScope(EscopoDeLog.De(
            "BatchId", batchId,
            "Arquivo", Path.GetFileName(emTrabalho)));

        CentralizeBatch lote;

        try
        {
            await using var stream = new FileStream(
                emTrabalho, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true);

            lote = await CentralizeJson.DeserializeBatchAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger.Here().Error(ex, "Arquivo com formato inválido.");
            store.Quarantine(emTrabalho, $"Formato inválido: {ex.Message}");
            return false;
        }
        catch (IOException ex)
        {
            logger.Here().Error(ex, "Falha ao ler o arquivo.");
            store.Quarantine(emTrabalho, $"Falha de leitura: {ex.Message}");
            return false;
        }

        var conferencia = CentralizeValidator.ValidateEnvelope(lote);

        if (!conferencia.IsValid)
        {
            logger.Here().Error("Lote recusado: {Motivo}", conferencia.ToMessage());
            store.Quarantine(emTrabalho, conferencia.ToMessage());
            return false;
        }

        var envelope = new IngestionEnvelope
        {
            BatchId = batchId,
            Origin = IngestionOrigin.Folder,
            Batch = lote,
            WorkingFilePath = emTrabalho,
            ReceivedAt = DateTimeOffset.Now
        };

        // Espera se a fila estiver cheia. É o comportamento certo aqui: o
        // arquivo já está seguro em disco, e segurar a varredura faz a entrada
        // desacelerar sozinha até o processamento vencer o acúmulo.
        await queue.EnqueueAsync(envelope, cancellationToken).ConfigureAwait(false);

        logger.Here().Info(
            "Lote aceito da pasta. Origem={Origem}, Itens={Itens}", lote.Source, lote.Items.Count);

        return true;
    }

    /// <summary>
    /// Diz se o arquivo já terminou de ser escrito.
    /// </summary>
    /// <remarks>
    /// Abrir sem compartilhar falha enquanto outro processo mantém o arquivo
    /// aberto. É o teste mais direto para o cenário real: o Builder ainda
    /// gravando o lote quando a varredura passa. Sem ele, leríamos um JSON
    /// pela metade e mandaríamos um lote bom para a quarentena.
    /// </remarks>
    private static bool EstaLiberado(string caminho)
    {
        try
        {
            using var _ = new FileStream(caminho, FileMode.Open, FileAccess.Read, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
