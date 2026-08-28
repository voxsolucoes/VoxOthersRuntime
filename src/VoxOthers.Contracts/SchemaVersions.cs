namespace VoxOthers.Contracts;

/// <summary>
/// Versionamento do contrato CentralizeEntity.
/// </summary>
/// <remarks>
/// O contrato completo é entregue na Fase 1. Aqui fica apenas a política de
/// versão, que precisa existir desde o início: todo lote que chega informa a
/// versão do schema, e o Runtime recusa versão desconhecida em vez de tentar
/// interpretar às cegas.
///
/// Regra de evolução: campo novo só entra como opcional. Remover campo ou
/// tornar obrigatório exige nova versão — backends de terceiros e do
/// Marketplace não são atualizados junto com o Runtime.
/// </remarks>
public static class SchemaVersions
{
    /// <summary>Versão emitida pelo Runtime atual.</summary>
    public const int Current = 1;

    /// <summary>Versões que o Runtime consegue processar.</summary>
    public static readonly IReadOnlySet<int> Supported = new HashSet<int> { 1 };

    /// <summary>Indica se o Runtime aceita a versão de schema informada.</summary>
    public static bool IsSupported(int schemaVersion) => Supported.Contains(schemaVersion);
}
