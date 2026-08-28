using Microsoft.Extensions.Options;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Registro das opções tipadas do Runtime.
/// </summary>
public static class ConfigurationServiceCollectionExtensions
{
    /// <summary>
    /// Liga as seções de configuração às classes tipadas, com validação
    /// executada no startup.
    /// </summary>
    /// <remarks>
    /// <c>ValidateOnStart</c> é o ponto central da Fase 0: sem ele, uma
    /// configuração errada só se manifestaria quando o primeiro item fosse
    /// processado — em produção, de madrugada, com gravação já perdida.
    /// Com ele, o serviço recusa subir e diz exatamente o que está errado.
    /// </remarks>
    public static IServiceCollection AddRuntimeConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RuntimeOptions>()
            .Bind(configuration.GetSection(RuntimeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<IngestionOptions>()
            .Bind(configuration.GetSection(IngestionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<RuntimeOptions>, RuntimeOptionsValidator>();
        services.AddSingleton<IValidateOptions<IngestionOptions>, IngestionOptionsValidator>();

        // Conexão com a base do Vox. Validada no boot: quem não tem a
        // connection string inteira precisa preencher os campos; quem tem
        // (ex.: perfil Carga) não precisa desta seção.
        services.AddOptions<VoxDatabaseOptions>()
            .Bind(configuration.GetSection(VoxDatabaseOptions.SectionName))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<VoxDatabaseOptions>, VoxDatabaseOptionsValidator>();

        return services;
    }
}
