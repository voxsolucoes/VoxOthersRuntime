using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Ingestion;
using VoxOthers.Runtime.Pipeline;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// As consultas de acompanhamento do serviço.
/// </summary>
/// <remarks>
/// <para>
/// São três, com públicos diferentes: <c>/api/v1/status</c> responde "o serviço
/// está dando conta?", <c>/api/v1/itens/{id}</c> responde "onde parou este
/// atendimento?" e <c>/metrics</c> entrega os mesmos números no formato que um
/// coletor entende.
/// </para>
/// <para>
/// <b>Todas pedem chave</b>, a mesma do webhook. A situação do serviço revela
/// volume por cliente, e o rastro de um item revela identificador de
/// atendimento e nome de operador — não é coisa para ficar aberta na rede. A
/// saúde (<c>/health/*</c>) continua aberta de propósito: quem a consulta é um
/// monitorador que só recebe "de pé" ou "com problema".
/// </para>
/// </remarks>
public static class DiagnosticsEndpoints
{
    public const string BasePath = "/api/v1";

    /// <summary>Endereço dos indicadores no formato de coletor.</summary>
    public const string MetricsPath = "/metrics";

    public static WebApplication MapDiagnostics(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var grupo = app.MapGroup(BasePath);

        grupo.MapGet("/status", SituacaoAsync);
        grupo.MapGet("/itens/{uniqueId}", RastrearAsync);

        app.MapGet(MetricsPath, Indicadores);

        return app;
    }

    /// <summary>
    /// Retrato do serviço agora.
    /// </summary>
    /// <remarks>
    /// A quarentena entra aqui, e não só nos indicadores, porque é a pergunta
    /// que se faz junto: "está acompanhando o volume?" e "ficou alguma coisa
    /// para trás?" são a mesma investigação, e separá-las em duas consultas só
    /// aumenta a chance de alguém olhar uma e esquecer a outra.
    /// </remarks>
    private static async Task<IResult> SituacaoAsync(
        HttpContext context,
        [FromServices] IOptionsMonitor<IngestionOptions> ingestao,
        [FromServices] IOptionsMonitor<RuntimeOptions> runtime,
        [FromServices] IngestionQueue fila,
        [FromServices] QuarantineReprocessor quarentena,
        [FromServices] MetricsRegistry indicadores,
        [FromServices] InicioDoServico inicio,
        [FromServices] TimeProvider tempo,
        [FromServices] ILoggerFactory registradores,
        CancellationToken cancellationToken)
    {
        if (Conferir(context, ingestao, registradores) is { } recusa)
        {
            return recusa;
        }

        var agora = tempo.GetLocalNow();
        var opcoes = runtime.CurrentValue;
        var resumo = await quarentena.ResumirAsync(cancellationToken);

        return Results.Ok(new
        {
            emPeDesde = inicio.Em,
            haQuantoTempo = Duracao(agora - inicio.Em),
            fila = new
            {
                lotes = fila.Count,
                capacidade = opcoes.ChannelCapacity,
                workers = opcoes.WorkerCount
            },
            jornada = new
            {
                ativa = opcoes.WorkingHours.Enabled,
                inicio = opcoes.WorkingHours.Start,
                fim = opcoes.WorkingHours.End,
                processandoAgora = !opcoes.WorkingHours.Enabled
                    || opcoes.WorkingHours.IsWithin(TimeOnly.FromDateTime(agora.DateTime))
            },
            quarentena = new
            {
                aguardando = resumo.Total,
                porDado = resumo.Dados,
                porAmbiente = resumo.Infraestrutura,
                maisAntigo = resumo.MaisAntigo
            },
            entrada = new
            {
                porPasta = ingestao.CurrentValue.Folder.Enabled,
                porWebhook = ingestao.CurrentValue.Webhook.Enabled
            },
            indicadores = indicadores.Ler().Select(s => new
            {
                nome = s.Nome,
                rotulos = s.Rotulos,
                valor = s.Valor,
                unidade = s.Unidade,
                ocorrencias = s.Ocorrencias,
                maior = s.Maior
            })
        });
    }

    private static async Task<IResult> RastrearAsync(
        HttpContext context,
        string uniqueId,
        [FromQuery] int? operacao,
        [FromServices] IOptionsMonitor<IngestionOptions> ingestao,
        [FromServices] ItemTracker rastreador,
        [FromServices] ILoggerFactory registradores,
        CancellationToken cancellationToken)
    {
        if (Conferir(context, ingestao, registradores) is { } recusa)
        {
            return recusa;
        }

        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            return Results.BadRequest(new { erro = "Informe o identificador do atendimento." });
        }

        var rastro = await rastreador.ProcurarAsync(uniqueId, operacao, cancellationToken);

        // Sempre 200, inclusive para "desconhecido". Um 404 diria que a consulta
        // não existe, quando na verdade ela respondeu — e a resposta "não há
        // registro deste item aqui" é uma informação útil, não um erro.
        return Results.Ok(rastro);
    }

    /// <summary>Os indicadores em texto, para um coletor.</summary>
    private static IResult Indicadores(
        HttpContext context,
        [FromServices] IOptionsMonitor<IngestionOptions> ingestao,
        [FromServices] MetricsRegistry indicadores,
        [FromServices] ILoggerFactory registradores)
    {
        if (Conferir(context, ingestao, registradores) is { } recusa)
        {
            return recusa;
        }

        return Results.Text(indicadores.EmTextoDeColetor(), "text/plain; version=0.0.4");
    }

    /// <summary>
    /// Recusa quem não tem chave. Devolve nulo quando pode seguir.
    /// </summary>
    /// <remarks>
    /// Mesma configuração de chaves do webhook e da quarentena. Manter três
    /// listas separadas garantiria que uma delas ficasse para trás numa troca de
    /// chave, e a que ficasse para trás seria a menos usada — exatamente esta.
    /// </remarks>
    private static IResult? Conferir(
        HttpContext context,
        IOptionsMonitor<IngestionOptions> ingestao,
        ILoggerFactory registradores)
    {
        ApiKeyGuard.Autenticar(context, ingestao.CurrentValue.Webhook, out var falha);

        if (falha is null)
        {
            return null;
        }

        registradores
            .CreateLogger(typeof(DiagnosticsEndpoints).FullName!)
            .Here().Warn("Consulta de acompanhamento recusada em {Caminho}: {Motivo}",
                context.Request.Path, falha);

        return Results.Json(new { erro = falha }, statusCode: StatusCodes.Status401Unauthorized);
    }

    /// <summary>Tempo em pé, escrito para ser lido por gente.</summary>
    private static string Duracao(TimeSpan tempo)
        => tempo.TotalDays >= 1
            ? $"{(int)tempo.TotalDays}d {tempo.Hours}h {tempo.Minutes}min"
            : tempo.TotalHours >= 1
                ? $"{(int)tempo.TotalHours}h {tempo.Minutes}min"
                : $"{(int)tempo.TotalMinutes}min";
}
