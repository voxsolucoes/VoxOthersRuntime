using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Observability;

namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// A fila interna entre a entrada e o processamento.
/// </summary>
/// <remarks>
/// <para>
/// Fila com capacidade fixa, e não ilimitada. É o que garante que a memória do
/// serviço é definida por configuração, e não pelo volume que a origem resolver
/// mandar. Cheia, ela segura quem está entrando em vez de o processo crescer
/// até morrer.
/// </para>
/// <para>
/// Uma leitora só e várias escritoras: a varredura de pasta e o webhook
/// escrevem, e os trabalhadores da Fase 3 leem. O canal já resolve a disputa
/// entre eles, o que dispensa bloqueio manual.
/// </para>
/// </remarks>
public sealed class IngestionQueue
{
    private readonly Channel<IngestionEnvelope> _channel;
    private readonly RuntimeMetrics _metricas;

    public IngestionQueue(IOptions<RuntimeOptions> options, RuntimeMetrics metricas)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metricas);

        _metricas = metricas;

        _channel = Channel.CreateBounded<IngestionEnvelope>(
            new BoundedChannelOptions(options.Value.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });

        // A fila se oferece ao indicador em vez de ser lida por ele. O caminho
        // contrário — as métricas recebendo a fila — fecharia um ciclo entre os
        // dois registros, e o contêiner recusaria os dois.
        metricas.AcompanharFila(() => Count);
    }

    /// <summary>Quantos lotes estão esperando processamento.</summary>
    /// <remarks>
    /// É o principal alarme preventivo do serviço: se este número sobe e não
    /// desce, o volume que chega passou a capacidade de processar — e dá para
    /// agir antes de virar problema.
    /// </remarks>
    public int Count => _channel.Reader.Count;

    /// <summary>
    /// Enfileira esperando o tempo que for preciso.
    /// </summary>
    /// <remarks>
    /// Usado pela pasta monitorada: o arquivo já está seguro em disco, não há
    /// pressa, e esperar é exatamente o comportamento desejado — a varredura
    /// desacelera sozinha até o processamento vencer a fila.
    /// </remarks>
    public async ValueTask EnqueueAsync(IngestionEnvelope envelope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        await _channel.Writer.WriteAsync(envelope, cancellationToken);

        Contabilizar(envelope);
    }

    /// <summary>
    /// Tenta enfileirar sem esperar. Devolve falso se a fila está cheia.
    /// </summary>
    /// <remarks>
    /// Usado pelo webhook, onde esperar seria pior: a conexão ficaria presa até
    /// o cliente desistir por tempo esgotado, e ele não saberia se o lote foi
    /// aceito. Recusar na hora, com resposta clara para tentar de novo depois,
    /// devolve a decisão a quem enviou.
    /// </remarks>
    public bool TryEnqueue(IngestionEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (!_channel.Writer.TryWrite(envelope))
        {
            return false;
        }

        Contabilizar(envelope);
        return true;
    }

    /// <summary>Consome os lotes conforme eles chegam.</summary>
    public IAsyncEnumerable<IngestionEnvelope> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Retira um lote sem esperar. Falso quando a fila está vazia.
    /// </summary>
    /// <remarks>
    /// É o que permite o desligamento sem perda: no encerramento o worker
    /// esvazia o que já está aqui em vez de abandonar a fila. Com
    /// <see cref="ReadAllAsync"/> isso não daria — o cancelamento
    /// interromperia a leitura junto com tudo o mais.
    /// </remarks>
    public bool TryDequeue([NotNullWhen(true)] out IngestionEnvelope? envelope)
        => _channel.Reader.TryRead(out envelope);

    /// <summary>
    /// Espera até haver algo para ler. Falso quando a fila foi fechada e já
    /// esvaziou.
    /// </summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
        => _channel.Reader.WaitToReadAsync(cancellationToken);

    /// <summary>
    /// Avisa que não entra mais nada. Quem está lendo termina o que sobrou na
    /// fila e só então encerra — é a base do desligamento sem perda.
    /// </summary>
    public void CompleteWriting() => _channel.Writer.TryComplete();

    /// <summary>
    /// Conta o que entrou.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A contagem mora aqui, e não nos três lugares que enfileiram, porque a
    /// fila é por onde <b>todo</b> lote passa — pasta, webhook e devolução da
    /// quarentena. Contar em cada entrada funcionaria hoje e deixaria de
    /// funcionar na quarta forma de entrada que alguém acrescentasse.
    /// </para>
    /// <para>
    /// E é depois de aceitar, nunca antes: lote recusado por fila cheia não
    /// chegou a entrar, e contá-lo faria os recebidos jamais fecharem com a
    /// soma dos desfechos.
    /// </para>
    /// </remarks>
    private void Contabilizar(IngestionEnvelope envelope)
        => _metricas.LoteRecebido(envelope.Origin, envelope.Source, envelope.ItemCount);
}
