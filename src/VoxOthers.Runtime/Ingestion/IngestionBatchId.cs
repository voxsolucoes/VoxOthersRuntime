namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// Gera o identificador de lote usado para correlacionar o log.
/// </summary>
public static class IngestionBatchId
{
    /// <summary>Cria um identificador novo.</summary>
    /// <remarks>
    /// Usa a versão 7 do GUID, que embute o horário de criação. Isso faz os
    /// identificadores ficarem em ordem cronológica quando alguém os organiza
    /// numa investigação — vantagem concreta sobre o GUID comum, que sai
    /// embaralhado, e sem o risco de repetição de um contador.
    /// </remarks>
    public static string New() => Guid.CreateVersion7().ToString("n");
}
