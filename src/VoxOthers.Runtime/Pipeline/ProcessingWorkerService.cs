using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Ingestion;
using VoxOthers.Runtime.Observability;
using VoxOthers.Runtime.Sinks;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>
/// Consome os lotes da fila, valida e entrega ao bilhete.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nenhum item se perde ao desligar.</b> O <c>stoppingToken</c> do host não é
/// usado para ler a fila nem para processar o item — se fosse, parar o serviço
/// descartaria o que já tinha sido aceito. Ele serve só para dizer "não pegue
/// mais nada novo depois de esvaziar". O que corta de verdade é o prazo de
/// encerramento do host, e só quando ele estoura.
/// </para>
/// <para>
/// <b>Falha de um item não derruba o lote.</b> Cada item é tratado por conta
/// própria: o que falha vai para a quarentena e os demais seguem. Antes, uma
/// exceção de infraestrutura no meio do lote abortava tudo o que vinha depois.
/// </para>
/// <para>
/// <b>Todo item tem um desfecho contado.</b> Sai daqui como importado, duplicado
/// ou recusado — nunca sem classificação. É o que faz a conta fechar: recebidos
/// menos os três desfechos é o que ainda está em trânsito, e um número que não
/// fecha aponta um caminho de saída que alguém esqueceu de contabilizar.
/// </para>
/// </remarks>
public class ProcessingWorkerService : BackgroundService
{
    /// <summary>De quanto em quanto tempo se confere se a jornada começou.</summary>
    /// <remarks>
    /// Um minuto é suficiente: a jornada é configurada em horas e minutos, e
    /// conferir com mais frequência só gastaria CPU para esperar.
    /// </remarks>
    private static readonly TimeSpan IntervaloDaJornada = TimeSpan.FromMinutes(1);

    private readonly IngestionQueue _queue;
    private readonly IngestionFileStore _store;
    private readonly IServiceProvider _serviceProvider;
    private readonly TransientRetry _retry;
    private readonly IOptionsMonitor<RuntimeOptions> _options;
    private readonly TimeProvider _tempo;
    private readonly RuntimeMetrics _metricas;
    private readonly ILogger<ProcessingWorkerService> _logger;

    /// <summary>
    /// Corte forçado. Só é acionado quando o prazo de encerramento do host
    /// estoura, e é o único jeito de interromper um item no meio.
    /// </summary>
    private readonly CancellationTokenSource _corte = new();

    public ProcessingWorkerService(
        IngestionQueue queue,
        IngestionFileStore store,
        IServiceProvider serviceProvider,
        TransientRetry retry,
        IOptionsMonitor<RuntimeOptions> options,
        TimeProvider tempo,
        RuntimeMetrics metricas,
        ILogger<ProcessingWorkerService> logger)
    {
        _queue = queue;
        _store = store;
        _serviceProvider = serviceProvider;
        _retry = retry;
        _options = options;
        _tempo = tempo;
        _metricas = metricas;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var quantos = _options.CurrentValue.WorkerCount;

        _logger.Here().Info("Iniciando {Quantos} workers de processamento.", quantos);

        await Task.WhenAll(Enumerable.Range(0, quantos)
            .Select(i => RunWorkerAsync(i, stoppingToken)));

        _logger.Here().Info("Todos os workers de processamento foram encerrados.");
    }

    /// <summary>
    /// Encerramento: espera a fila esvaziar antes de deixar o serviço morrer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O <see cref="BackgroundService.StopAsync"/> da base aguarda o
    /// <see cref="ExecuteAsync"/> terminar ou o prazo do host estourar. Como os
    /// workers só saem quando a fila está vazia, o efeito é o desligamento sem
    /// perda que a Fase 5 promete.
    /// </para>
    /// <para>
    /// <b>Vale ajustar o prazo do host</b> (<c>HostOptions.ShutdownTimeout</c>):
    /// no padrão de 5 segundos, um lote grande não termina e o corte forçado
    /// acontece de qualquer jeito.
    /// </para>
    /// </remarks>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.Here().Info(
            "Encerramento pedido. {Pendentes} lote(s) na fila serão processados antes de sair.",
            _queue.Count);

        // Quando o prazo do host estourar, aí sim interrompe no meio.
        using var corteNoPrazo = cancellationToken.Register(() =>
        {
            _logger.Here().Warn(
                "O prazo de encerramento estourou com {Pendentes} lote(s) ainda na fila. " +
                "Os lotes vindos de pasta voltam para a entrada no próximo boot; " +
                "os recebidos por webhook precisam ser reenviados.",
                _queue.Count);

            _corte.Cancel();
        });

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _corte.Dispose();
        base.Dispose();
    }

    private async Task RunWorkerAsync(int indice, CancellationToken stoppingToken)
    {
        _logger.Here().Info("Worker {Indice} iniciado.", indice);

        try
        {
            while (!_corte.IsCancellationRequested)
            {
                var desligando = stoppingToken.IsCancellationRequested;

                // Fora da jornada o processamento espera — mas não durante o
                // desligamento: aí o que importa é esvaziar a fila, porque lote
                // recebido por webhook só existe em memória.
                if (!desligando)
                {
                    await AguardarJornadaAsync(stoppingToken);
                }

                if (_queue.TryDequeue(out var envelope))
                {
                    await ProcessarLoteAsync(envelope, _corte.Token);
                    continue;
                }

                // Fila vazia. Se estamos desligando, acabou o trabalho.
                if (desligando)
                {
                    break;
                }

                try
                {
                    if (!await _queue.WaitToReadAsync(stoppingToken))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Desligamento pedido enquanto esperava. Volta ao laço para
                    // esvaziar o que porventura entrou e então sair.
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Here().Warn("Worker {Indice} interrompido pelo corte de encerramento.", indice);
        }
        catch (Exception ex)
        {
            _logger.Here().Error(ex, "Erro não tratado no worker {Indice}.", indice);
        }
        finally
        {
            _logger.Here().Info("Worker {Indice} finalizado.", indice);
        }
    }

    /// <summary>
    /// Segura o processamento até a jornada de trabalho começar.
    /// </summary>
    /// <remarks>
    /// A entrada continua aceitando durante a pausa; o que espera é só o
    /// processamento. Registrar em log apenas na entrada e na saída da pausa é
    /// deliberado: uma linha por minuto durante a madrugada inteira esconderia
    /// tudo o que interessa no arquivo de log.
    /// </remarks>
    private async Task AguardarJornadaAsync(CancellationToken stoppingToken)
    {
        var jornada = _options.CurrentValue.WorkingHours;

        if (jornada.IsWithin(Agora()))
        {
            return;
        }

        _logger.Here().Info(
            "Fora da jornada ({Inicio} às {Fim}). O processamento espera; a entrada continua aceitando.",
            jornada.Start, jornada.End);

        while (!stoppingToken.IsCancellationRequested
               && !_corte.IsCancellationRequested
               && !_options.CurrentValue.WorkingHours.IsWithin(Agora()))
        {
            try
            {
                await Task.Delay(IntervaloDaJornada, _tempo, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (!stoppingToken.IsCancellationRequested && !_corte.IsCancellationRequested)
        {
            _logger.Here().Info("Jornada começou. Retomando o processamento.");
        }
    }

    private TimeOnly Agora() => TimeOnly.FromDateTime(_tempo.GetLocalNow().DateTime);

    /// <summary>
    /// Processa um lote inteiro.
    /// </summary>
    /// <remarks>
    /// O escopo de log aberto aqui é o que amarra a investigação: enquanto o
    /// lote está sendo processado, <b>toda</b> linha registrada por qualquer
    /// classe — inclusive as de dentro da validação e da entrega — sai com o
    /// lote, a origem e a forma de entrada junto. Sem ele, o log de três workers
    /// trabalhando ao mesmo tempo vira uma lista intercalada em que não se sabe
    /// qual linha pertence a qual atendimento.
    /// </remarks>
    private async Task ProcessarLoteAsync(IngestionEnvelope envelope, CancellationToken cancellationToken)
    {
        using var atividade = RuntimeMetrics.Rastro.StartActivity("lote.processar");

        atividade?.SetTag("vox.lote", envelope.BatchId);
        atividade?.SetTag("vox.origem", envelope.Origin.ToString());
        atividade?.SetTag("vox.fonte", envelope.Source);
        atividade?.SetTag("vox.itens", envelope.ItemCount);

        using var _ = _logger.BeginScope(EscopoDeLog.De(
            "BatchId", envelope.BatchId,
            "Origem", envelope.Origin.ToString(),
            "Fonte", envelope.Source));

        using var scope = _serviceProvider.CreateScope();

        var sink = scope.ServiceProvider.GetRequiredService<IImportSink>();
        var pipeline = scope.ServiceProvider.GetRequiredService<IValidationPipeline>();
        var ledger = scope.ServiceProvider.GetRequiredService<IImportLedger>();
        var quarentena = scope.ServiceProvider.GetRequiredService<IItemQuarantine>();

        try
        {
            foreach (var item in envelope.Batch.Items)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.Here().Warn(
                        "Lote {BatchId} interrompido no meio pelo corte de encerramento.", envelope.BatchId);
                    break;
                }

                await ProcessarItemAsync(
                    item, envelope, pipeline, sink, ledger, quarentena, cancellationToken);
            }

            _logger.Here().Info(
                "Lote concluído: {BatchId}. Itens: {Itens}.", envelope.BatchId, envelope.Batch.Items.Count);

            Concluir(envelope);
        }
        catch (Exception ex)
        {
            // Chegar aqui é anormal: cada item já trata a própria falha. Só
            // sobra o que aconteceu fora do laço.
            _logger.Here().Error(ex, "Erro ao processar o lote {BatchId}.", envelope.BatchId);

            atividade?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);

            if (envelope.WorkingFilePath is not null)
            {
                _store.Quarantine(envelope.WorkingFilePath, ex.Message);
            }
        }
        finally
        {
            // No "finally" de propósito: o lote que estourou também terminou, e
            // deixá-lo fora da conta faria os recebidos e os concluídos
            // divergirem justamente quando algo deu errado — que é quando
            // alguém está olhando o indicador.
            _metricas.LoteConcluido(envelope.Origin, envelope.Source);
        }
    }

    /// <summary>
    /// Tira o arquivo do lote da pasta de trabalho.
    /// </summary>
    /// <remarks>
    /// Sem isto o arquivo fica parado na pasta de trabalho para sempre, e o
    /// <c>RecoverAbandoned</c> do próximo boot o devolve para a entrada — o
    /// serviço reprocessaria tudo a cada reinício. A deduplicação evitaria o
    /// bilhete duplicado, mas o trabalho seria refeito e a pasta cresceria sem
    /// parar.
    /// </remarks>
    private void Concluir(IngestionEnvelope envelope)
    {
        if (envelope.WorkingFilePath is null)
        {
            // Lote de webhook: não existe arquivo para mover.
            return;
        }

        _store.Complete(envelope.WorkingFilePath);
    }

    /// <summary>
    /// Processa um item e contabiliza o desfecho dele.
    /// </summary>
    /// <remarks>
    /// A medição de tempo e a contagem ficam aqui, num lugar só, e o trabalho de
    /// verdade fica em <see cref="ExecutarItemAsync"/>. Espalhar
    /// <c>_metricas.Item...</c> pelos quatro pontos de saída do processamento
    /// funcionaria hoje e deixaria de funcionar no primeiro caminho novo que
    /// alguém acrescentasse sem lembrar de contar.
    /// </remarks>
    private async Task ProcessarItemAsync(
        CentralizeEntity item,
        IngestionEnvelope envelope,
        IValidationPipeline pipeline,
        IImportSink sink,
        IImportLedger ledger,
        IItemQuarantine quarentena,
        CancellationToken cancellationToken)
    {
        using var atividade = RuntimeMetrics.Rastro.StartActivity("item.importar");

        atividade?.SetTag("vox.item", item.UniqueId);
        atividade?.SetTag("vox.operacao", item.OperationId);

        // O identificador entra no escopo, e não só nas mensagens que o citam:
        // as linhas registradas lá dentro pela validação e pela entrega passam a
        // sair com ele também.
        using var _ = _logger.BeginScope(EscopoDeLog.De("UniqueId", item.UniqueId));

        var inicio = _tempo.GetTimestamp();

        var desfecho = await ExecutarItemAsync(
            item, envelope, pipeline, sink, ledger, quarentena, cancellationToken);

        var duracao = _tempo.GetElapsedTime(inicio);

        atividade?.SetTag("vox.desfecho", desfecho.Tipo.ToString());

        switch (desfecho.Tipo)
        {
            case Desfecho.Importado:
                _metricas.ItemImportado(envelope.Source, duracao);
                break;

            case Desfecho.Duplicado:
                _metricas.ItemDuplicado(envelope.Source, duracao);
                break;

            default:
                _metricas.ItemRecusado(envelope.Source, desfecho.Recusa, duracao);
                atividade?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, desfecho.Recusa.ToString());
                break;
        }
    }

    private async Task<ResultadoDoItem> ExecutarItemAsync(
        CentralizeEntity item,
        IngestionEnvelope envelope,
        IValidationPipeline pipeline,
        IImportSink sink,
        IImportLedger ledger,
        IItemQuarantine quarentena,
        CancellationToken cancellationToken)
    {
        ValidationResult validacao;

        try
        {
            // A validação captura os erros de infraestrutura por dentro e os
            // devolve no resultado, sem estourar. Por isso a nova tentativa
            // olha o resultado, e não só a exceção.
            validacao = await _retry.ExecutarAsync(
                ct => pipeline.ValidateAsync(item, envelope.Batch.Source, ct),
                r => !r.IsValid && !r.IsDuplicate && r.FailureKind == QuarantineKind.Infraestrutura,
                $"validação do item {item.UniqueId}",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var tipo = ValidationPipeline.Classificar(ex);

            await GuardarAsync(item, envelope, tipo, ex.Message, quarentena, cancellationToken);

            return ResultadoDoItem.Recusado(tipo);
        }

        if (!validacao.IsValid)
        {
            // Duplicata não é falha: o item já está no Vox e não há o que guardar.
            if (validacao.IsDuplicate)
            {
                _logger.Here().Info("Item {UniqueId} ignorado: já importado.", item.UniqueId);

                return ResultadoDoItem.Duplicado;
            }

            var motivo = string.Join("; ", validacao.Errors);

            _logger.Here().Warn("Validação falhou: {UniqueId}. Erros: {Erros}", item.UniqueId, motivo);

            await GuardarAsync(item, envelope, validacao.FailureKind, motivo, quarentena, cancellationToken);

            return ResultadoDoItem.Recusado(validacao.FailureKind);
        }

        return await EntregarAsync(validacao.Context!, envelope, sink, ledger, quarentena, cancellationToken);
    }

    /// <summary>
    /// Entrega o item e só então o registra como importado.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ordem importa e é deliberada. Registrar antes de entregar criaria a
    /// pior falha possível: o item ficaria marcado como importado sem que a
    /// gravação tivesse chegado ao Vox, e a deduplicação impediria qualquer
    /// nova tentativa — a gravação se perderia em silêncio. Na ordem oposta, o
    /// pior caso é o bilhete sair duas vezes, que é visível e recuperável.
    /// </para>
    /// <para>
    /// <b>Repetir a entrega é seguro</b> porque tudo o que ela faz antes de
    /// gravar o bilhete é idempotente: mídia e anexo que já estão na árvore de
    /// gravação são mantidos, não recopiados. O bilhete é o último passo, então
    /// uma falha depois dele não existe para ser repetida.
    /// </para>
    /// </remarks>
    private async Task<ResultadoDoItem> EntregarAsync(
        ImportedItemContext context,
        IngestionEnvelope envelope,
        IImportSink sink,
        IImportLedger ledger,
        IItemQuarantine quarentena,
        CancellationToken cancellationToken)
    {
        string reference;

        try
        {
            reference = await _retry.ExecutarAsync(
                ct => sink.ProcessAsync(context, ct),
                _ => false,
                $"entrega do item {context.UniqueId}",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Here().Error(ex, "Erro ao processar o item {UniqueId}: {Motivo}",
                context.UniqueId, ex.Message);

            var tipo = ValidationPipeline.Classificar(ex);

            await GuardarAsync(context.Entity, envelope, tipo, ex.Message, quarentena, cancellationToken);

            return ResultadoDoItem.Recusado(tipo);
        }

        try
        {
            await _retry.ExecutarAsync(
                async ct => await ledger.RegistrarAsync(context, reference, ct),
                _ => false,
                $"registro do item {context.UniqueId}",
                cancellationToken);

            _logger.Here().Info("Item importado: {UniqueId} -> {Reference}", context.UniqueId, reference);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // O bilhete já está na pasta de registro e vai ser importado. O que
            // falhou foi a anotação de que ele saiu — então numa reexecução
            // este item sairá de novo. Fica registrado como erro justamente
            // porque é uma duplicata em potencial, não uma perda.
            _logger.Here().Error(ex,
                "Bilhete {Reference} do item {UniqueId} foi entregue, mas não foi possível " +
                "registrá-lo como importado. O item pode ser reprocessado.",
                reference, context.UniqueId);
        }

        // Importado nos dois casos: o bilhete saiu. Falhar só a anotação é
        // problema de duplicata futura, não de item que não entrou — contá-lo
        // como recusa mandaria alguém procurar uma gravação que está lá.
        return ResultadoDoItem.Importado;
    }

    /// <summary>
    /// Guarda o item que não entrou, sem deixar a quarentena derrubar o lote.
    /// </summary>
    /// <remarks>
    /// A quarentena escreve em disco e disco também falha. Se ela falhar, o
    /// registro vai para o log com o motivo original junto — é o último lugar em
    /// que a informação ainda existe. Deixar a exceção subir aqui interromperia
    /// o processamento dos demais itens do lote por causa de um que já tinha
    /// falhado.
    /// </remarks>
    private async Task GuardarAsync(
        CentralizeEntity item,
        IngestionEnvelope envelope,
        QuarantineKind tipo,
        string motivo,
        IItemQuarantine quarentena,
        CancellationToken cancellationToken)
    {
        try
        {
            await quarentena.QuarantineAsync(
                item, envelope.BatchId, envelope.Batch.Source, tipo, motivo, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Here().Error(ex,
                "Item {UniqueId} do lote {BatchId} falhou ({Tipo}: {Motivo}) e ainda não foi " +
                "possível guardá-lo na quarentena. Este log é o único registro do item.",
                item.UniqueId, envelope.BatchId, tipo, motivo);
        }
    }

    /// <summary>Como um item terminou.</summary>
    private enum Desfecho
    {
        Importado,
        Duplicado,
        Recusado
    }

    /// <summary>
    /// O desfecho de um item, com a natureza da recusa quando houve uma.
    /// </summary>
    private readonly record struct ResultadoDoItem(Desfecho Tipo, QuarantineKind Recusa)
    {
        public static ResultadoDoItem Importado => new(Desfecho.Importado, default);

        public static ResultadoDoItem Duplicado => new(Desfecho.Duplicado, default);

        public static ResultadoDoItem Recusado(QuarantineKind tipo) => new(Desfecho.Recusado, tipo);
    }
}
