using System.Diagnostics;
using System.Diagnostics.Metrics;
using VoxOthers.Runtime.Ingestion;
using VoxOthers.Runtime.Pipeline;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Os indicadores do serviço: quanto entrou, quanto saiu, quanto está esperando
/// e por quê.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que a API do .NET e não o SDK do OpenTelemetry.</b> Estes tipos —
/// <see cref="Meter"/>, <see cref="Counter{T}"/>, <see cref="Histogram{T}"/> —
/// <i>são</i> a API de métricas do OpenTelemetry em .NET; o padrão foi adotado
/// pela plataforma. O que o SDK acrescenta é o exportador, e é só isso que fica
/// de fora aqui: uma dúzia de pacotes para falar um protocolo que a instalação
/// do cliente não tem quem escute. O serviço roda em servidor Windows na casa
/// do cliente, e o que existe lá para olhar é um navegador.
/// </para>
/// <para>
/// A conta muda no dia em que houver um coletor: como a instrumentação já é a
/// do OpenTelemetry, ligar o exportador é acrescentar o pacote e uma chamada no
/// <c>Program</c>. <b>Nenhuma linha desta classe muda</b> — e é justamente por
/// isso que ela foi escrita assim, em vez de com contadores próprios.
/// </para>
/// <para>
/// <b>Cuidado com rótulo.</b> Cada combinação de valores de rótulo vira uma
/// série guardada em memória para sempre. Por isso só entram como rótulo
/// valores de conjunto fechado — origem, fonte emissora, tipo de recusa. O
/// motivo da recusa, que é texto livre vindo de exceção, <b>não</b> é rótulo:
/// ele viraria uma série nova por mensagem de erro distinta e faria a memória
/// crescer sem limite. Motivo se lê no log e na quarentena, que é onde ele cabe.
/// </para>
/// </remarks>
public sealed class RuntimeMetrics
{
    /// <summary>Nome do medidor e da fonte de rastreamento.</summary>
    public const string Nome = "VoxOthers.Runtime";

    /// <summary>
    /// Fonte de rastreamento das operações.
    /// </summary>
    /// <remarks>
    /// Estática porque <see cref="ActivitySource"/> é identificado pelo nome e
    /// não guarda estado por instância. O ganho concreto sem nenhum coletor
    /// ligado: quando existe uma atividade em curso, o registrador anexa
    /// <c>TraceId</c> e <c>SpanId</c> às linhas de log, o que amarra o que
    /// aconteceu em paralelo nos vários workers.
    /// </remarks>
    public static readonly ActivitySource Rastro = new(Nome);

    private readonly Counter<long> _lotesRecebidos;
    private readonly Counter<long> _lotesConcluidos;
    private readonly Counter<long> _itensRecebidos;
    private readonly Counter<long> _itensImportados;
    private readonly Counter<long> _itensDuplicados;
    private readonly Counter<long> _itensRecusados;
    private readonly Histogram<double> _duracaoDoItem;

    /// <summary>
    /// De onde o medidor de fila lê o número.
    /// </summary>
    /// <remarks>
    /// A fila não é injetada aqui de propósito: ela depende deste objeto para
    /// se contabilizar, e injetá-la de volta fecharia um ciclo que o contêiner
    /// recusaria. Quem sabe o próprio tamanho é a fila, então é ela que se
    /// oferece — a leitura só acontece quando alguém consulta o indicador.
    /// </remarks>
    private Func<int>? _tamanhoDaFila;

    public RuntimeMetrics(IMeterFactory fabrica)
    {
        ArgumentNullException.ThrowIfNull(fabrica);

        Medidor = fabrica.Create(Nome);

        _lotesRecebidos = Medidor.CreateCounter<long>(
            "voxothers.lotes.recebidos", "lotes",
            "Lotes aceitos na fila, por forma de entrada e origem emissora.");

        _lotesConcluidos = Medidor.CreateCounter<long>(
            "voxothers.lotes.concluidos", "lotes",
            "Lotes cujo processamento terminou, tendo os itens entrado ou não.");

        _itensRecebidos = Medidor.CreateCounter<long>(
            "voxothers.itens.recebidos", "itens",
            "Atendimentos aceitos na fila.");

        _itensImportados = Medidor.CreateCounter<long>(
            "voxothers.itens.importados", "itens",
            "Atendimentos que viraram bilhete no Vox.");

        _itensDuplicados = Medidor.CreateCounter<long>(
            "voxothers.itens.duplicados", "itens",
            "Atendimentos ignorados por já terem sido importados antes.");

        _itensRecusados = Medidor.CreateCounter<long>(
            "voxothers.itens.recusados", "itens",
            "Atendimentos que foram para a quarentena, por natureza da recusa.");

        _duracaoDoItem = Medidor.CreateHistogram<double>(
            "voxothers.item.duracao", "ms",
            "Tempo do item, da saída da fila até o desfecho.");

        // A soma de recebidos menos importados, duplicados e recusados é o que
        // ainda está em trânsito. Só fecha se este indicador for lido junto dos
        // outros, e é por isso que ele mora aqui e não num endereço à parte.
        Medidor.CreateObservableGauge(
            "voxothers.fila.lotes",
            () => _tamanhoDaFila?.Invoke() ?? 0,
            "lotes",
            "Lotes esperando processamento. Subindo sem descer, o volume passou a capacidade.");
    }

    /// <summary>
    /// O medidor desta instância.
    /// </summary>
    /// <remarks>
    /// Exposto para o coletor conseguir escutar <b>este</b> medidor, e não
    /// qualquer um que tenha o mesmo nome. Faz diferença no teste, em que vários
    /// serviços sobem lado a lado no mesmo processo: filtrando por nome, um teste
    /// contaria o movimento do outro.
    /// </remarks>
    internal Meter Medidor { get; }

    /// <summary>
    /// Liga o indicador de fila à fila de verdade.
    /// </summary>
    /// <remarks>
    /// A última fila registrada vence. No serviço isso não acontece — a fila é
    /// única —, mas em teste é comum montar mais de uma sobre o mesmo medidor, e
    /// então o indicador passa a mostrar a mais recente.
    /// </remarks>
    internal void AcompanharFila(Func<int> leitura) => _tamanhoDaFila = leitura;

    /// <summary>Um lote entrou na fila.</summary>
    public void LoteRecebido(IngestionOrigin origem, string fonte, int itens)
    {
        var rotulos = new TagList { { "origem", origem.ToString() }, { "fonte", Fonte(fonte) } };

        _lotesRecebidos.Add(1, rotulos);
        _itensRecebidos.Add(itens, rotulos);
    }

    /// <summary>Um lote terminou de ser processado.</summary>
    public void LoteConcluido(IngestionOrigin origem, string fonte)
        => _lotesConcluidos.Add(1,
            new TagList { { "origem", origem.ToString() }, { "fonte", Fonte(fonte) } });

    /// <summary>O item virou bilhete.</summary>
    public void ItemImportado(string fonte, TimeSpan duracao)
    {
        _itensImportados.Add(1, new TagList { { "fonte", Fonte(fonte) } });
        Duracao(fonte, "importado", duracao);
    }

    /// <summary>O item já estava no Vox.</summary>
    public void ItemDuplicado(string fonte, TimeSpan duracao)
    {
        _itensDuplicados.Add(1, new TagList { { "fonte", Fonte(fonte) } });
        Duracao(fonte, "duplicado", duracao);
    }

    /// <summary>O item foi para a quarentena.</summary>
    public void ItemRecusado(string fonte, QuarantineKind tipo, TimeSpan duracao)
    {
        _itensRecusados.Add(1,
            new TagList { { "fonte", Fonte(fonte) }, { "tipo", tipo.ToString() } });

        Duracao(fonte, "recusado", duracao);
    }

    private void Duracao(string fonte, string desfecho, TimeSpan duracao)
        => _duracaoDoItem.Record(duracao.TotalMilliseconds,
            new TagList { { "fonte", Fonte(fonte) }, { "desfecho", desfecho } });

    /// <summary>
    /// A origem emissora vem do lote, ou seja, de fora.
    /// </summary>
    /// <remarks>
    /// Vazio viraria uma série sem nome, difícil de interpretar depois. E o
    /// valor entra em rótulo, então convém que ele seja o nome cadastrado e não
    /// uma variação com espaço sobrando.
    /// </remarks>
    private static string Fonte(string fonte)
        => string.IsNullOrWhiteSpace(fonte) ? "(sem origem)" : fonte.Trim();
}
