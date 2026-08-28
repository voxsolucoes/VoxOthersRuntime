namespace VoxOthers.Contracts;

/// <summary>
/// Envelope de entrega: um conjunto de atendimentos enviados juntos pelo
/// Builder, seja como arquivo na pasta monitorada, seja como corpo de uma
/// chamada ao webhook.
/// </summary>
/// <remarks>
/// O envelope existe para que o Runtime saiba, antes de olhar o conteúdo,
/// com qual versão de contrato está lidando e de onde o lote veio. Sem ele,
/// evoluir o contrato exigiria adivinhação.
/// </remarks>
public sealed class CentralizeBatch
{
    /// <summary>
    /// Versão do contrato usada para montar este lote.
    /// </summary>
    public int SchemaVersion { get; init; } = SchemaVersions.Current;

    /// <summary>
    /// Quem produziu o lote — nome do conector no Builder (ex.: "genesys-acme").
    /// </summary>
    /// <remarks>
    /// Serve para diagnóstico: com dezenas de origens alimentando o mesmo
    /// Runtime, saber qual delas gerou um lote problemático é a diferença
    /// entre um ajuste pontual e uma caça ao tesouro no log.
    /// </remarks>
    public string Source { get; init; } = string.Empty;

    /// <summary>Momento em que o Builder montou o lote.</summary>
    public DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Atendimentos do lote.</summary>
    public IReadOnlyList<CentralizeEntity> Items { get; init; } = [];
}
