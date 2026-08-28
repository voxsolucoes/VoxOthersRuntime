using System.Text.Json;
using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Ingestion;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>Retrato do que está parado na quarentena.</summary>
public sealed record QuarantineSummary
{
    /// <summary>Itens aguardando decisão.</summary>
    public required int Total { get; init; }

    /// <summary>Dos quais recusados por problema no dado.</summary>
    public required int Dados { get; init; }

    /// <summary>Dos quais recusados por falha de ambiente.</summary>
    public required int Infraestrutura { get; init; }

    /// <summary>Arquivos que não puderam ser lidos.</summary>
    public required int Ilegiveis { get; init; }

    /// <summary>Data do item mais antigo, quando há algum.</summary>
    public DateTimeOffset? MaisAntigo { get; init; }
}

/// <summary>Resultado de um pedido de reprocessamento.</summary>
public sealed record ReprocessOutcome
{
    /// <summary>Itens devolvidos para a fila de processamento.</summary>
    public required int Reenfileirados { get; init; }

    /// <summary>Itens deixados onde estavam por não casarem com o filtro.</summary>
    public required int Ignorados { get; init; }

    /// <summary>Arquivos que não puderam ser lidos e continuam na quarentena.</summary>
    public required int Ilegiveis { get; init; }

    /// <summary>
    /// Verdadeiro quando a fila encheu e o reenvio parou antes do limite. O que
    /// sobrou continua na quarentena e pode ser reenviado depois.
    /// </summary>
    public required bool ParouPorFilaCheia { get; init; }
}

/// <summary>
/// Devolve itens da quarentena para a fila de processamento.
/// </summary>
/// <remarks>
/// <para>
/// É o outro lado da quarentena. Guardar o item que falhou só tem valor se
/// existir um caminho de volta; sem isso, a pasta vira um cemitério que alguém
/// teria de reprocessar copiando arquivo na mão para a entrada — que é
/// exatamente o que se faz hoje no sistema atual.
/// </para>
/// <para>
/// <b>O item é retirado da quarentena antes de entrar na fila</b>, movido para a
/// subpasta <c>reprocessados</c>. A ordem importa: mover é indivisível, então
/// dois pedidos simultâneos nunca reenviam o mesmo item, e o arquivo não fica
/// disponível para um terceiro pedido enquanto o primeiro ainda processa. Se a
/// fila recusar, o arquivo volta para o lugar.
/// </para>
/// <para>
/// <b>Um envelope por item</b>, e não um lote com todos. O envelope de lote
/// carrega origem e momento de geração próprios, e juntar itens de origens
/// diferentes num só falsearia os dois. Item isolado também significa que uma
/// nova falha atinge só ele.
/// </para>
/// </remarks>
public sealed class QuarantineReprocessor
{
    /// <summary>Subpasta para onde vai o item já devolvido à fila.</summary>
    public const string PastaDeReprocessados = "reprocessados";

    /// <summary>Teto de itens por pedido, mesmo que peçam mais.</summary>
    /// <remarks>
    /// A fila tem capacidade fixa e é compartilhada com a entrada normal. Um
    /// pedido sem teto empurraria milhares de itens de uma vez e faria o
    /// webhook começar a recusar lotes novos — a recuperação do passado
    /// atrapalharia o presente.
    /// </remarks>
    public const int LimiteMaximo = 1_000;

    private readonly IOptionsMonitor<QuarantineOptions> _options;
    private readonly IngestionQueue _queue;
    private readonly TimeProvider _tempo;
    private readonly ILogger<QuarantineReprocessor> _logger;

    public QuarantineReprocessor(
        IOptionsMonitor<QuarantineOptions> options,
        IngestionQueue queue,
        TimeProvider tempo,
        ILogger<QuarantineReprocessor> logger)
    {
        _options = options;
        _queue = queue;
        _tempo = tempo;
        _logger = logger;
    }

    /// <summary>
    /// Conta o que está parado, por tipo de recusa.
    /// </summary>
    /// <remarks>
    /// Abre cada arquivo, então não é para chamar em laço — é a consulta que
    /// alguém faz antes de decidir reprocessar. O tipo da recusa está dentro do
    /// arquivo e não no nome; colocá-lo no nome deixaria esta conta mais barata
    /// e quebraria a leitura de tudo o que já foi guardado.
    /// </remarks>
    public async Task<QuarantineSummary> ResumirAsync(CancellationToken cancellationToken)
    {
        var dados = 0;
        var infraestrutura = 0;
        var ilegiveis = 0;
        DateTimeOffset? maisAntigo = null;

        foreach (var arquivo in Listar())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var registro = await LerAsync(arquivo, cancellationToken);

            if (registro is null)
            {
                ilegiveis++;
                continue;
            }

            if (registro.Kind == QuarantineKind.Dados)
            {
                dados++;
            }
            else
            {
                infraestrutura++;
            }

            if (maisAntigo is null || registro.QuarantinedAt < maisAntigo)
            {
                maisAntigo = registro.QuarantinedAt;
            }
        }

        return new QuarantineSummary
        {
            Total = dados + infraestrutura,
            Dados = dados,
            Infraestrutura = infraestrutura,
            Ilegiveis = ilegiveis,
            MaisAntigo = maisAntigo
        };
    }

    /// <summary>
    /// Devolve itens da quarentena para a fila.
    /// </summary>
    /// <param name="apenas">
    /// Restringe a um tipo de recusa. Nulo reenvia os dois tipos.
    /// </param>
    /// <param name="limite">Quantos itens reenviar no máximo.</param>
    public async Task<ReprocessOutcome> ReprocessarAsync(
        QuarantineKind? apenas,
        int limite,
        CancellationToken cancellationToken)
    {
        var teto = Math.Clamp(limite, 1, LimiteMaximo);

        var reenfileirados = 0;
        var ignorados = 0;
        var ilegiveis = 0;
        var filaCheia = false;

        foreach (var arquivo in Listar())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reenfileirados >= teto)
            {
                break;
            }

            var registro = await LerAsync(arquivo, cancellationToken);

            if (registro is null)
            {
                // Fica onde está: um arquivo ilegível é a única pista de que
                // alguma coisa deu errado ao guardá-lo.
                ilegiveis++;
                continue;
            }

            if (apenas is not null && registro.Kind != apenas)
            {
                ignorados++;
                continue;
            }

            var reservado = Reservar(arquivo);

            if (reservado is null)
            {
                // Outro pedido levou o arquivo entre a listagem e agora.
                ignorados++;
                continue;
            }

            if (!_queue.TryEnqueue(Montar(registro)))
            {
                Devolver(reservado, arquivo);
                filaCheia = true;
                break;
            }

            reenfileirados++;
        }

        _logger.Here().Info(
            "Reprocessamento da quarentena: {Reenfileirados} reenviado(s), {Ignorados} ignorado(s), " +
            "{Ilegiveis} ilegível(is), filaCheia={FilaCheia}.",
            reenfileirados, ignorados, ilegiveis, filaCheia);

        return new ReprocessOutcome
        {
            Reenfileirados = reenfileirados,
            Ignorados = ignorados,
            Ilegiveis = ilegiveis,
            ParouPorFilaCheia = filaCheia
        };
    }

    /// <summary>
    /// Arquivos aguardando decisão, do mais antigo para o mais novo.
    /// </summary>
    /// <remarks>
    /// Do mais antigo primeiro porque um pedido com limite atende só uma parte:
    /// atacando sempre pela ponta antiga, chamadas sucessivas cobrem a pasta
    /// inteira. Pela ordem contrária, o item do fundo nunca sairia.
    /// </remarks>
    private List<string> Listar()
    {
        var raiz = _options.CurrentValue.Path;

        if (string.IsNullOrWhiteSpace(raiz) || !Directory.Exists(raiz))
        {
            return [];
        }

        var reprocessados = Path.Combine(raiz, PastaDeReprocessados);

        return
        [
            .. Directory.EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
                .Where(a => Path.GetExtension(a).Equals(".json", StringComparison.OrdinalIgnoreCase))
                .Where(a => !a.StartsWith(reprocessados + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderBy(File.GetLastWriteTimeUtc)
        ];
    }

    /// <summary>
    /// Lê o arquivo com <b>as mesmas</b> opções usadas para gravá-lo.
    /// </summary>
    /// <remarks>
    /// Reaproveitar a configuração de serialização da quarentena, em vez de
    /// declarar outra igual aqui, é o que garante que os dois lados nunca
    /// divirjam — mudar o formato lá passa a valer aqui sem ninguém lembrar.
    /// </remarks>
    private async Task<ItemEmQuarentena?> LerAsync(string arquivo, CancellationToken cancellationToken)
    {
        try
        {
            await using var conteudo = File.OpenRead(arquivo);

            return await JsonSerializer.DeserializeAsync<ItemEmQuarentena>(
                conteudo, ItemQuarantine.Formato, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Here().Warn(ex, "Arquivo de quarentena ilegível: {Caminho}", arquivo);
            return null;
        }
    }

    /// <summary>
    /// Tira o arquivo da quarentena e o guarda como já reenviado. Devolve o
    /// novo caminho, ou nulo se não deu para reservar.
    /// </summary>
    private string? Reservar(string arquivo)
    {
        try
        {
            var destino = Path.Combine(
                _options.CurrentValue.Path,
                PastaDeReprocessados,
                $"{_tempo.GetLocalNow():yyyy-MM-dd}",
                Path.GetFileName(arquivo));

            Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

            File.Move(arquivo, destino, overwrite: false);

            return destino;
        }
        catch (Exception ex)
        {
            _logger.Here().Warn(ex, "Não foi possível reservar {Caminho} para reprocessamento.", arquivo);
            return null;
        }
    }

    /// <summary>
    /// Recoloca na quarentena o item que não coube na fila.
    /// </summary>
    /// <remarks>
    /// Falhar aqui é grave e por isso é erro no log: o item não entrou na fila e
    /// também não está mais onde quem opera vai procurar. Ele continua existindo
    /// em <c>reprocessados</c> — e só não some porque o expurgo dessa subpasta
    /// tem prazo próprio.
    /// </remarks>
    private void Devolver(string reservado, string original)
    {
        try
        {
            File.Move(reservado, original, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.Here().Error(ex,
                "O item de {Reservado} não coube na fila e não foi possível devolvê-lo para {Original}.",
                reservado, original);
        }
    }

    private IngestionEnvelope Montar(ItemEmQuarentena registro) => new()
    {
        BatchId = IngestionBatchId.New(),
        Origin = IngestionOrigin.Quarantine,
        Batch = new CentralizeBatch
        {
            Source = registro.Source,
            GeneratedAt = registro.QuarantinedAt,
            Items = [registro.Item]
        },
        ReceivedAt = _tempo.GetLocalNow()

        // WorkingFilePath fica nulo de propósito: o arquivo desta reentrada é o
        // da quarentena, e quem cuida dele é esta classe. Apontar para ele faria
        // o worker tentar movê-lo para a pasta de concluídos da entrada por
        // pasta, que é outro fluxo.
    };
}
