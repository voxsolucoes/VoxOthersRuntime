using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Data;
using VoxOthers.Runtime.Registration;
using VoxOthers.Runtime.Sinks;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>
/// Extensão de DI para registrar os serviços da Fase 3 (pipeline de processamento).
/// </summary>
public static class PipelineServiceCollectionExtensions
{
    public static IServiceCollection AddProcessingPipeline(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // A base do Vox é Firebird — conferido contra a base de homologação, não
        // presumido. O mapeamento estava em SQL Server e nunca teria funcionado
        // em produção: erraria já na abertura da conexão.
        // Duas formas de configurar a base, e a mais explícita vence:
        // 1) ConnectionStrings:VoxDatabase — a string inteira (perfil Carga);
        // 2) seção VoxDatabase, campo a campo — o Runtime monta a string.
        var connString = configuration.GetConnectionString("VoxDatabase");
        if (string.IsNullOrWhiteSpace(connString))
        {
            // A validação de boot garante os campos obrigatórios quando não
            // há a string inteira.
            var voxDb = configuration.GetSection(VoxDatabaseOptions.SectionName).Get<VoxDatabaseOptions>();
            connString = VoxDatabaseConnectionString.Compose(voxDb ?? new VoxDatabaseOptions());
        }

        services.AddDbContext<VoxDbContext>(options =>
            options.UseFirebird(connString));

        // Saída em bilhete .GRF. Validado no boot pelo mesmo motivo do resto da
        // configuração: pasta de registro errada só apareceria no primeiro item
        // processado, horas depois de subir o serviço.
        services.AddOptions<GrfOptions>()
            .Bind(configuration.GetSection(GrfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Quarentena: também validada no boot. Descobrir que a pasta não existe
        // justamente no momento em que um item falhou seria perder o item por
        // causa do erro de configuração, e não do erro original.
        services.AddOptions<QuarantineOptions>()
            .Bind(configuration.GetSection(QuarantineOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Deduplicação: pasta compartilhada entre as instâncias. Sem caminho
        // configurado o serviço não sobe — subir sem deduplicação significaria
        // reimportar tudo no primeiro reprocessamento, e ninguém perceberia até
        // o bilhete duplicado aparecer no Vox.
        services.AddOptions<DeduplicationOptions>()
            .Bind(configuration.GetSection(DeduplicationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Sem estado e sem dependência — uma instância serve a todos os escopos.
        services.AddSingleton<GrfTicketFactory>();
        services.AddSingleton<ChatTicketFactory>();
        services.AddSingleton<WhatsAppChatTicketFactory>();
        services.AddSingleton<TicketPublisher>();
        services.AddSingleton<IMediaPlacement, MediaPlacement>();
        services.AddSingleton<IAttachmentPlacement, AttachmentPlacement>();
        services.AddSingleton<IItemQuarantine, ItemQuarantine>();

        // O livro de importados deixou de depender do DbContext quando saiu da
        // base para o disco, então também é singleton.
        services.AddSingleton<IImportLedger, ImportLedger>();

        // Relógio injetável. É o que permite testar a jornada de trabalho e a
        // espera entre tentativas sem o teste levar minutos de verdade.
        services.TryAddSingleton(TimeProvider.System);

        // Política de nova tentativa para falha passageira.
        services.AddSingleton<TransientRetry>();

        // Reprocessamento da quarentena e expurgo do histórico. Os dois são
        // singleton: não guardam estado entre chamadas e trabalham só com
        // arquivo, então uma instância por processo basta.
        services.AddSingleton<QuarantineReprocessor>();
        services.AddSingleton<RetentionCleanup>();
        services.AddHostedService<RetentionCleanupService>();

        // Serviços de negócio.
        services.AddScoped<IVoxRegistration, VoxRegistration>();
        services.AddScoped<IValidationPipeline, ValidationPipeline>();

        // Um sink por tipo de atendimento, e o roteador na frente. Quem consome
        // pede IImportSink e não precisa saber que existe mais de um.
        services.AddScoped<GrfImportSink>();
        services.AddScoped<ChatImportSink>();
        services.AddScoped<WhatsAppChatImportSink>();
        services.AddScoped<IImportSink, ImportSinkRouter>();

        // Worker de processamento.
        services.AddHostedService<ProcessingWorkerService>();

        return services;
    }
}
