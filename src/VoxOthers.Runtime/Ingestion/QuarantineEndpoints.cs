using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Pipeline;

namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// As rotas que enxergam e reprocessam a quarentena.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que HTTP.</b> É o botão de reprocessar. Sem ele, recuperar itens de um
/// dia em que o banco caiu significaria abrir a pasta e copiar arquivo na mão
/// para a entrada — que é o procedimento do sistema atual e a origem de metade
/// dos incidentes de duplicidade. Uma rota deixa a operação registrada em log,
/// com filtro e limite, e permite que o painel de monitoramento chame sozinho.
/// </para>
/// <para>
/// <b>Publicadas mesmo com o webhook desligado.</b> A entrada por pasta e o
/// reprocessamento são coisas diferentes: quem não usa webhook continua
/// precisando do botão.
/// </para>
/// </remarks>
public static class QuarantineEndpoints
{
    /// <summary>Prefixo das rotas de quarentena.</summary>
    public const string BasePath = "/api/v1/quarentena";

    /// <summary>Publica as rotas de consulta e reprocessamento.</summary>
    public static WebApplication MapQuarantineEndpoints(this WebApplication app)
    {
        var grupo = app.MapGroup(BasePath);

        grupo.MapGet("/", ResumirAsync);
        grupo.MapPost("/reprocessar", ReprocessarAsync);

        return app;
    }

    private static async Task<IResult> ResumirAsync(
        HttpContext context,
        [FromServices] IOptionsMonitor<IngestionOptions> options,
        [FromServices] QuarantineReprocessor reprocessador,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("VoxOthers.Runtime.Ingestion.Quarentena");
        var recusa = Conferir(context, options, logger);

        if (recusa is not null)
        {
            return recusa;
        }

        var resumo = await reprocessador.ResumirAsync(cancellationToken);

        return Results.Ok(new
        {
            total = resumo.Total,
            dados = resumo.Dados,
            infraestrutura = resumo.Infraestrutura,
            ilegiveis = resumo.Ilegiveis,
            maisAntigo = resumo.MaisAntigo
        });
    }

    private static async Task<IResult> ReprocessarAsync(
        HttpContext context,
        [FromQuery] string? tipo,
        [FromQuery] int? limite,
        [FromServices] IOptionsMonitor<IngestionOptions> options,
        [FromServices] QuarantineReprocessor reprocessador,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("VoxOthers.Runtime.Ingestion.Quarentena");
        var recusa = Conferir(context, options, logger);

        if (recusa is not null)
        {
            return recusa;
        }

        if (!TentarInterpretar(tipo, out var filtro))
        {
            return Results.Json(
                new
                {
                    erro = $"Tipo '{tipo}' não reconhecido.",
                    aceitos = new[] { "infraestrutura", "dados", "todos" }
                },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var resultado = await reprocessador.ReprocessarAsync(
            filtro, limite ?? LimitePadrao, cancellationToken);

        return Results.Ok(new
        {
            reenfileirados = resultado.Reenfileirados,
            ignorados = resultado.Ignorados,
            ilegiveis = resultado.Ilegiveis,
            parouPorFilaCheia = resultado.ParouPorFilaCheia
        });
    }

    /// <summary>Quantos itens um pedido sem limite explícito reenvia.</summary>
    /// <remarks>
    /// Cem é o suficiente para um incidente comum e pequeno o bastante para não
    /// tomar a fila da entrada normal. Quem precisa de mais pede de novo, ou
    /// informa o limite.
    /// </remarks>
    private const int LimitePadrao = 100;

    /// <summary>
    /// Traduz o filtro pedido. Sem filtro significa <b>só infraestrutura</b>.
    /// </summary>
    /// <remarks>
    /// O padrão não é "tudo" de propósito. Item recusado por problema no dado
    /// vai falhar exatamente igual se for reenviado sem que nada tenha mudado na
    /// origem — o reenvio só gera trabalho e um arquivo novo de quarentena.
    /// Recusado por falha de ambiente é o oposto: normalmente entra de primeira
    /// assim que o ambiente volta. Quem quer reenviar dado quebrado diz isso
    /// explicitamente, porque é uma decisão, não o caso comum.
    /// </remarks>
    private static bool TentarInterpretar(string? tipo, out QuarantineKind? filtro)
    {
        filtro = null;

        if (string.IsNullOrWhiteSpace(tipo))
        {
            filtro = QuarantineKind.Infraestrutura;
            return true;
        }

        switch (tipo.Trim().ToLowerInvariant())
        {
            case "infraestrutura":
                filtro = QuarantineKind.Infraestrutura;
                return true;

            case "dados":
                filtro = QuarantineKind.Dados;
                return true;

            case "todos":
                filtro = null;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Recusa o acesso sem chave válida. Devolve nulo quando pode seguir.
    /// </summary>
    /// <remarks>
    /// Usa a mesma configuração de chaves do webhook. Reprocessar injeta
    /// gravação no Vox tanto quanto enviar um lote, então proteger com menos
    /// rigor não faria sentido — e manter as chaves num lugar só evita que uma
    /// revogação esqueça metade das portas.
    /// </remarks>
    private static IResult? Conferir(
        HttpContext context,
        IOptionsMonitor<IngestionOptions> options,
        ILogger logger)
    {
        ApiKeyGuard.Autenticar(context, options.CurrentValue.Webhook, out var falha);

        if (falha is null)
        {
            return null;
        }

        logger.Here().Warn("Acesso à quarentena recusado: {Motivo}", falha);

        return Results.Json(new { erro = falha }, statusCode: StatusCodes.Status401Unauthorized);
    }
}
