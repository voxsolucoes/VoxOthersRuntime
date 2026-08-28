using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// A entrada por rede: o Builder envia o lote e recebe na hora se foi aceito.
/// </summary>
public static class WebhookEndpoints
{
    /// <summary>Cabeçalho que carrega a chave de acesso da origem.</summary>
    public const string ApiKeyHeader = ApiKeyGuard.Header;

    /// <summary>Publica o endpoint, se ele estiver habilitado.</summary>
    public static WebApplication MapCentralizeWebhook(this WebApplication app)
    {
        var webhook = app.Services.GetRequiredService<IOptions<IngestionOptions>>().Value.Webhook;

        if (!webhook.Enabled)
        {
            return app;
        }

        app.MapPost(webhook.Path, ReceberLoteAsync);

        return app;
    }

    private static async Task<IResult> ReceberLoteAsync(
        HttpContext context,
        [FromServices] IOptionsMonitor<IngestionOptions> options,
        [FromServices] IngestionQueue queue,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var webhook = options.CurrentValue.Webhook;
        var logger = loggerFactory.CreateLogger("VoxOthers.Runtime.Ingestion.Webhook");

        var origemAutenticada = ApiKeyGuard.Autenticar(context, webhook, out var falhaDeAcesso);

        if (falhaDeAcesso is not null)
        {
            logger.Here().Warn("Envio recusado: {Motivo}", falhaDeAcesso);
            return Results.Json(new { erro = falhaDeAcesso }, statusCode: StatusCodes.Status401Unauthorized);
        }

        CentralizeBatch lote;

        try
        {
            lote = await CentralizeJson.DeserializeBatchAsync(context.Request.Body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            logger.Here().Warn(ex, "Envio com corpo inválido.");
            return Results.Json(
                new { erro = "Corpo fora do contrato.", detalhe = ex.Message },
                statusCode: StatusCodes.Status400BadRequest);
        }

        var conferencia = CentralizeValidator.ValidateEnvelope(lote);

        if (!conferencia.IsValid)
        {
            logger.Here().Warn("Envio recusado: {Motivo}", conferencia.ToMessage());
            return Results.Json(
                new { erro = "Lote fora do contrato.", problemas = conferencia.Errors },
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (lote.Items.Count > webhook.MaxBatchSize)
        {
            var mensagem =
                $"O lote tem {lote.Items.Count} atendimentos e o limite é {webhook.MaxBatchSize}. " +
                "Divida em envios menores.";

            logger.Here().Warn("Envio recusado: {Motivo}", mensagem);
            return Results.Json(new { erro = mensagem }, statusCode: StatusCodes.Status413PayloadTooLarge);
        }

        // A origem declarada no corpo tem de bater com a dona da chave usada.
        // Sem isso, um backend com chave válida poderia enviar lotes se
        // passando por outro, e a rastreabilidade por origem valeria nada.
        if (origemAutenticada is not null
            && !string.Equals(origemAutenticada, lote.Source, StringComparison.OrdinalIgnoreCase))
        {
            var mensagem =
                $"A chave usada pertence à origem '{origemAutenticada}', mas o lote se declara " +
                $"da origem '{lote.Source}'.";

            logger.Here().Warn("Envio recusado: {Motivo}", mensagem);
            return Results.Json(new { erro = mensagem }, statusCode: StatusCodes.Status403Forbidden);
        }

        var envelope = new IngestionEnvelope
        {
            BatchId = IngestionBatchId.New(),
            Origin = IngestionOrigin.Webhook,
            Batch = lote,
            ReceivedAt = DateTimeOffset.Now
        };

        // Não espera a fila esvaziar: a conexão ficaria presa até o cliente
        // desistir por tempo esgotado, sem saber se o lote entrou. Recusar na
        // hora devolve a decisão a quem enviou.
        if (!queue.TryEnqueue(envelope))
        {
            logger.Here().Warn(
                "Envio recusado por fila cheia. Origem={Origem}, Itens={Itens}",
                lote.Source, lote.Items.Count);

            context.Response.Headers.RetryAfter = "30";

            return Results.Json(
                new { erro = "O serviço está com a fila cheia. Tente novamente em instantes." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        logger.Here().Info(
            "Lote aceito pelo webhook. BatchId={BatchId}, Origem={Origem}, Itens={Itens}",
            envelope.BatchId, lote.Source, lote.Items.Count);

        // 202, e não 200: o lote foi aceito para processamento, não importado.
        // Dizer "importei" aqui seria mentira, e o Builder tomaria decisão
        // errada com base nisso.
        return Results.Accepted(
            value: new
            {
                batchId = envelope.BatchId,
                itens = lote.Items.Count,
                situacao = "aceito para processamento"
            });
    }
}
