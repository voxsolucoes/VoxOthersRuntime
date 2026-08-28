using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>O que uma passada de expurgo fez.</summary>
public sealed record CleanupReport
{
    /// <summary>Marcadores de itens importados que passaram da idade.</summary>
    public int MarcadoresApagados { get; init; }

    /// <summary>Cópias de itens já reenviados que passaram da idade.</summary>
    public int ReprocessadosApagados { get; init; }

    /// <summary>Subpastas que ficaram vazias e foram removidas.</summary>
    public int PastasApagadas { get; init; }

    /// <summary>Arquivos que não puderam ser apagados nesta passada.</summary>
    public int Falhas { get; init; }

    /// <summary>Se alguma coisa foi efetivamente removida.</summary>
    public bool Mexeu => MarcadoresApagados > 0 || ReprocessadosApagados > 0 || PastasApagadas > 0;
}

/// <summary>
/// Expurgo por idade das pastas de controle.
/// </summary>
/// <remarks>
/// <para>
/// Duas pastas crescem para sempre se ninguém cuidar: a dos marcadores de item
/// importado (um arquivo por atendimento) e a das cópias de itens já reenviados
/// pela quarentena. Nenhuma das duas tem um dono natural para limpar — não são
/// arquivo de log, que a rotina de sistema recolhe —, e a que mais cresce fica
/// em unidade de rede compartilhada.
/// </para>
/// <para>
/// <b>O que esta classe nunca toca:</b> os itens em quarentena que ainda
/// aguardam decisão. São dado que não entrou no Vox; apagar por idade seria
/// perder o atendimento em silêncio, que é o oposto do propósito da quarentena.
/// Só a subpasta <c>reprocessados</c> entra no expurgo.
/// </para>
/// <para>
/// A regra é a data da última escrita do arquivo, e não uma data escrita
/// dentro dele. É mais barata (não precisa abrir nada), funciona igual para o
/// marcador e para o item, e não depende de o conteúdo estar legível.
/// </para>
/// </remarks>
public sealed class RetentionCleanup
{
    private readonly IOptionsMonitor<DeduplicationOptions> _deduplicacao;
    private readonly IOptionsMonitor<QuarantineOptions> _quarentena;
    private readonly TimeProvider _tempo;
    private readonly ILogger<RetentionCleanup> _logger;

    public RetentionCleanup(
        IOptionsMonitor<DeduplicationOptions> deduplicacao,
        IOptionsMonitor<QuarantineOptions> quarentena,
        TimeProvider tempo,
        ILogger<RetentionCleanup> logger)
    {
        _deduplicacao = deduplicacao;
        _quarentena = quarentena;
        _tempo = tempo;
        _logger = logger;
    }

    /// <summary>
    /// Passa uma vez pelas duas pastas e apaga o que passou da idade.
    /// </summary>
    /// <remarks>
    /// Síncrono de propósito. Não existe operação de arquivo assíncrona de
    /// verdade aqui — apagar arquivo é chamada de sistema imediata —, e uma
    /// assinatura <c>async</c> só daria a impressão errada de que dá para
    /// intercalar trabalho enquanto ela roda.
    /// </remarks>
    public CleanupReport Limpar(CancellationToken cancellationToken)
    {
        var deduplicacao = _deduplicacao.CurrentValue;
        var quarentena = _quarentena.CurrentValue;

        var marcadores = LimparPasta(
            deduplicacao.Path,
            deduplicacao.RetentionDays,
            "marcadores de itens importados",
            cancellationToken);

        var reprocessados = LimparPasta(
            PastaDeReprocessados(quarentena.Path),
            quarentena.ReprocessedRetentionDays,
            "cópias de itens já reenviados",
            cancellationToken);

        var relatorio = new CleanupReport
        {
            MarcadoresApagados = marcadores.Arquivos,
            ReprocessadosApagados = reprocessados.Arquivos,
            PastasApagadas = marcadores.Pastas + reprocessados.Pastas,
            Falhas = marcadores.Falhas + reprocessados.Falhas
        };

        // Só registra quando fez alguma coisa. Uma linha por dia dizendo "nada
        // a apagar" vira ruído no log de um serviço que fica meses no ar.
        if (relatorio.Mexeu || relatorio.Falhas > 0)
        {
            _logger.Here().Info(
                "Expurgo concluído. Marcadores={Marcadores}, Reprocessados={Reprocessados}, " +
                "PastasVazias={Pastas}, Falhas={Falhas}",
                relatorio.MarcadoresApagados, relatorio.ReprocessadosApagados,
                relatorio.PastasApagadas, relatorio.Falhas);
        }

        return relatorio;
    }

    /// <summary>Subpasta com as cópias dos itens já reenviados.</summary>
    internal static string PastaDeReprocessados(string raizDaQuarentena)
        => string.IsNullOrWhiteSpace(raizDaQuarentena)
            ? string.Empty
            : Path.Combine(raizDaQuarentena, QuarantineReprocessor.PastaDeReprocessados);

    private (int Arquivos, int Pastas, int Falhas) LimparPasta(
        string raiz,
        int dias,
        string descricao,
        CancellationToken cancellationToken)
    {
        if (dias <= 0 || string.IsNullOrWhiteSpace(raiz) || !Directory.Exists(raiz))
        {
            return (0, 0, 0);
        }

        var limite = _tempo.GetUtcNow().UtcDateTime.AddDays(-dias);
        var apagados = 0;
        var falhas = 0;

        try
        {
            // Enumeração preguiçosa, e não uma lista de tudo: a pasta de
            // marcadores pode ter milhões de arquivos, e materializar a lista
            // custaria mais memória do que o serviço inteiro usa em operação.
            foreach (var arquivo in Directory.EnumerateFiles(raiz, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (File.GetLastWriteTimeUtc(arquivo) >= limite)
                    {
                        continue;
                    }

                    File.Delete(arquivo);
                    apagados++;
                }
                catch (Exception ex)
                {
                    // Arquivo em uso ou sem permissão. Não é motivo para parar o
                    // resto: a próxima passada tenta de novo.
                    falhas++;
                    _logger.Here().Debug(ex, "Não foi possível apagar {Caminho}.", arquivo);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A varredura em si falhou (pasta de rede caiu no meio, por
            // exemplo). O que já foi apagado está apagado; o resto fica para a
            // próxima.
            falhas++;
            _logger.Here().Warn(ex, "A varredura de {Descricao} em {Raiz} foi interrompida.", descricao, raiz);
        }

        var pastas = ApagarPastasVazias(raiz, cancellationToken);

        return (apagados, pastas, falhas);
    }

    /// <summary>
    /// Remove as subpastas que ficaram vazias, de baixo para cima.
    /// </summary>
    /// <remarks>
    /// Sem isto, apagar os arquivos resolveria metade do problema: a árvore de
    /// pastas continuaria com um diretório por operação e por dia, e listar a
    /// raiz seguiria custando caro. A raiz nunca é removida — ela é
    /// configuração, e recriá-la a cada uso esconderia um caminho errado.
    /// </remarks>
    private int ApagarPastasVazias(string raiz, CancellationToken cancellationToken)
    {
        var apagadas = 0;

        try
        {
            foreach (var pasta in Directory.EnumerateDirectories(raiz, "*", SearchOption.AllDirectories)
                         .OrderByDescending(Profundidade))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (Directory.EnumerateFileSystemEntries(pasta).Any())
                    {
                        continue;
                    }

                    Directory.Delete(pasta);
                    apagadas++;
                }
                catch (Exception ex)
                {
                    _logger.Here().Debug(ex, "Não foi possível apagar a pasta {Caminho}.", pasta);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Here().Debug(ex, "Não foi possível varrer as subpastas de {Raiz}.", raiz);
        }

        return apagadas;
    }

    /// <summary>
    /// Quantos níveis o caminho tem. Ordenar por isto, de trás para frente, é o
    /// que garante que a subpasta some antes da pasta que a contém — do
    /// contrário a pasta de cima nunca estaria vazia na hora de olhar.
    /// </summary>
    private static int Profundidade(string caminho)
        => caminho.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
}

/// <summary>
/// Roda o expurgo de tempos em tempos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que dentro do serviço</b> e não uma tarefa agendada do Windows: a
/// regra de qual pasta pode ser limpa, e qual não pode, mora aqui. Um script
/// externo apontado para a pasta errada apagaria itens em quarentena — dado
/// que não entrou no Vox — sem que nada acusasse.
/// </para>
/// <para>
/// <b>Espera antes da primeira passada.</b> O boot é o momento mais ocupado do
/// serviço: é quando a pasta de trabalho é recuperada e a primeira varredura de
/// entrada acontece. Uma passada completa numa pasta de milhões de arquivos
/// competindo com isso atrasaria a importação logo na subida.
/// </para>
/// </remarks>
public sealed class RetentionCleanupService : BackgroundService
{
    private static readonly TimeSpan EsperaInicial = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Intervalo = TimeSpan.FromHours(24);

    private readonly RetentionCleanup _expurgo;
    private readonly TimeProvider _tempo;
    private readonly ILogger<RetentionCleanupService> _logger;

    public RetentionCleanupService(
        RetentionCleanup expurgo,
        TimeProvider tempo,
        ILogger<RetentionCleanupService> logger)
    {
        _expurgo = expurgo;
        _tempo = tempo;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(EsperaInicial, _tempo, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                Executar(stoppingToken);

                await Task.Delay(Intervalo, _tempo, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Desligamento. O expurgo não tem nada a concluir: o que ele faz é
            // apagar arquivo, e parar no meio só deixa trabalho para a próxima.
        }
    }

    private void Executar(CancellationToken stoppingToken)
    {
        try
        {
            _expurgo.Limpar(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Falhar aqui não pode derrubar o expurgo para sempre. Sem este
            // bloco, um erro de permissão na primeira passada mataria o serviço
            // de fundo e a pasta voltaria a crescer sem limite — em silêncio.
            _logger.Here().Error(ex, "O expurgo falhou nesta passada. Será tentado de novo no próximo ciclo.");
        }
    }
}
