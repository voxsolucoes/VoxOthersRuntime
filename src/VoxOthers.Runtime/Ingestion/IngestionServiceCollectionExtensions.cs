namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// Registro das peças de entrada.
/// </summary>
public static class IngestionServiceCollectionExtensions
{
    /// <summary>Registra a fila, o gerenciador de arquivos e os serviços de entrada.</summary>
    public static IServiceCollection AddIngestion(this IServiceCollection services)
    {
        // A fila é única no processo: se cada consumidor recebesse a sua, a
        // entrada escreveria numa e o processamento leria de outra vazia.
        services.AddSingleton<IngestionQueue>();
        services.AddSingleton<IngestionFileStore>();

        services.AddHostedService<FolderIngestionService>();

        // O consumidor provisório da Fase 2 (PendingBatchDrainService) foi
        // removido aqui. Ele continuou registrado depois que a Fase 3 entrou e
        // disputava a mesma fila com o worker de verdade: um canal entrega cada
        // lote a UM leitor só, então parte dos lotes era retirada, registrada
        // como "concluída" e nunca importada — sem erro nenhum no log.

        return services;
    }
}
