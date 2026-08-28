namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Conexão com a base do Vox (Firebird) configurada campo a campo — quem
/// instala preenche as chaves, sem precisar montar uma connection string
/// (mesmo espírito do App.voxconfig antigo, com valores por chave).
/// </summary>
/// <remarks>
/// A conexão é resolvida em duas formas, e a mais explícita vence:
/// <list type="bullet">
///   <item><c>ConnectionStrings:VoxDatabase</c> (ou a variável de ambiente
///   <c>ConnectionStrings__VoxDatabase</c>): a string inteira, para quem já
///   tem pronta (é assim que o perfil de Carga entra).</item>
///   <item>esta seção: <see cref="VoxDatabaseConnectionString"/> monta a
///   string a partir dos campos.</item>
/// </list>
/// A validação (<see cref="VoxDatabaseOptionsValidator"/>) só exige os campos
/// quando não há a string inteira.
/// </remarks>
public sealed class VoxDatabaseOptions
{
    public const string SectionName = "VoxDatabase";

    /// <summary>Endereço do servidor Firebird (host ou IP).</summary>
    public string Server { get; init; } = string.Empty;

    /// <summary>Porta do Firebird (padrão: 3050).</summary>
    public int Port { get; init; } = 3050;

    /// <summary>Nome da base do Vox.</summary>
    public string Database { get; init; } = string.Empty;

    /// <summary>Usuário da base (no Vox, normalmente SYSDBA).</summary>
    public string User { get; init; } = string.Empty;

    /// <summary>Senha do usuário (vazia quando não houver).</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Charset da conexão. A base de homologação usa NONE.</summary>
    public string Charset { get; init; } = "NONE";

    /// <summary>Dialeto Firebird (1, 2 ou 3). O Vox usa 3.</summary>
    public int Dialect { get; init; } = 3;

    /// <summary>
    /// Habilita o pool de conexões do Firebird — mesmo desenho do FrameworkCIT
    /// (<c>clsDatabase</c>): manter conexões abertas entre operações evita o
    /// custo de conectar a cada chamada. Padrão: ligado.
    /// </summary>
    public bool Pooling { get; init; } = true;

    /// <summary>
    /// Mínimo de conexões que o pool mantém abertas de antemão
    /// (FrameworkCIT: 5).
    /// </summary>
    public int MinPoolSize { get; init; } = 5;

    /// <summary>
    /// Máximo de conexões simultâneas do pool. Acima disso a chamada espera até
    /// liberar ou estourar o timeout (FrameworkCIT: 50).
    /// </summary>
    public int MaxPoolSize { get; init; } = 50;

    /// <summary>
    /// Vida útil de cada conexão no pool, em segundos. Depois disso o provedor
    /// fecha e revalida, evitando conexão morta por firewall/timeout
    /// (FrameworkCIT: 300 = 5 minutos).
    /// </summary>
    public int ConnectionLifetimeSeconds { get; init; } = 300;

    /// <summary>
    /// Tempo máximo para abrir uma conexão, em segundos. Protege contra o
    /// servidor de banco fora do ar travando o worker para sempre
    /// (FrameworkCIT: 30).
    /// </summary>
    public int ConnectionTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Desliga transações distribuídas (enlist): o Runtime não usa MSDTC, e
    /// desligar evita tentativa de transação coordenada com o serviço do
    /// Firebird. Mesmo valor do FrameworkCIT.
    /// </summary>
    public bool Enlist { get; init; } = false;
}
