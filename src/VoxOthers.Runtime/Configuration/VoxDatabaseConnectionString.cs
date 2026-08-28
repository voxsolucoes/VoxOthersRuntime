namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Monta a connection string Firebird a partir dos campos de
/// <see cref="VoxDatabaseOptions"/>. Formato aceito pelo provedor Firebird do
/// EF Core (FirebirdSql): chave=valor separados por ponto-e-vírgula, último
/// ponto-e-vírgula incluso.
/// </summary>
/// <remarks>
/// O pool é configurado explicitamente, com os mesmos parâmetros do
/// FrameworkCIT (<c>clsDatabase</c>): pooling ligado, mínimo/máximo de
/// conexões, vida útil e timeout de abertura. Sem isso o provedor usa os
/// padrões internos, que não garantem o mesmo comportamento validado em
/// produção no sistema atual.
/// </remarks>
public static class VoxDatabaseConnectionString
{
    public static string Compose(VoxDatabaseOptions o)
    {
        return $"DataSource={o.Server};Port={o.Port};Database={o.Database};" +
               $"User={o.User};Password={o.Password};Charset={o.Charset};Dialect={o.Dialect};" +
               $"Pooling={o.Pooling};Min Pool Size={o.MinPoolSize};Max Pool Size={o.MaxPoolSize};" +
               $"Connection lifetime={o.ConnectionLifetimeSeconds};Connection timeout={o.ConnectionTimeoutSeconds};" +
               $"Enlist={o.Enlist};";
    }
}
