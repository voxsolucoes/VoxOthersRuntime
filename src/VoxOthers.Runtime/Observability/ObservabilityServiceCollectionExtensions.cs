using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Registra o acompanhamento do serviço.
/// </summary>
public static class ObservabilityServiceCollectionExtensions
{
    public static IServiceCollection AddObservability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // O host já registra a fábrica de medidores; a chamada é idempotente e
        // está aqui para que este bloco funcione sozinho, inclusive em teste que
        // monte o contêiner na mão.
        services.AddMetrics();

        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<RuntimeMetrics>();
        services.AddSingleton<MetricsRegistry>();
        services.AddSingleton<ItemTracker>();
        services.AddSingleton<InicioDoServico>();

        return services;
    }
}

/// <summary>
/// Quando o serviço subiu.
/// </summary>
/// <remarks>
/// <para>
/// Parece supérfluo e é a primeira coisa que se pergunta quando alguém diz que
/// "parou de importar": um serviço que subiu há dois minutos explica sozinho a
/// fila vazia e o contador zerado, e evita uma investigação inteira na direção
/// errada.
/// </para>
/// <para>
/// Vem do <see cref="TimeProvider"/>, e não do relógio do processo, porque é o
/// mesmo relógio que a jornada de trabalho usa — em teste, os dois precisam
/// concordar.
/// </para>
/// </remarks>
public sealed class InicioDoServico
{
    public InicioDoServico(TimeProvider tempo) => Em = tempo.GetLocalNow();

    public DateTimeOffset Em { get; }
}
