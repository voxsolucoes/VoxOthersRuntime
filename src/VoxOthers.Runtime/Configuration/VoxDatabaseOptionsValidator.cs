using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Valida a configuração da base. Os campos só são exigidos quando não há uma
/// connection string inteira (<c>ConnectionStrings:VoxDatabase</c>) — quem já
/// a tem (ex.: perfil Carga) não precisa preencher nada nesta seção.
/// </summary>
public sealed class VoxDatabaseOptionsValidator : IValidateOptions<VoxDatabaseOptions>
{
    private readonly IConfiguration _configuration;

    public VoxDatabaseOptionsValidator(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ValidateOptionsResult Validate(string? name, VoxDatabaseOptions options)
    {
        var temStringInteira = !string.IsNullOrWhiteSpace(_configuration.GetConnectionString("VoxDatabase"));
        if (temStringInteira)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Server))
            failures.Add("VoxDatabase:Server é obrigatório — endereço do servidor Firebird (host ou IP).");

        if (string.IsNullOrWhiteSpace(options.Database))
            failures.Add("VoxDatabase:Database é obrigatório — nome da base do Vox.");

        if (string.IsNullOrWhiteSpace(options.User))
            failures.Add("VoxDatabase:User é obrigatório — usuário da base (SYSDBA, ...).");

        if (options.Port is < 1 or > 65535)
            failures.Add($"VoxDatabase:Port inválido ({options.Port}) — use entre 1 e 65535.");

        if (options.Dialect is < 1 or > 3)
            failures.Add($"VoxDatabase:Dialect inválido ({options.Dialect}) — use 1, 2 ou 3.");

        if (options.MinPoolSize < 0)
            failures.Add($"VoxDatabase:MinPoolSize inválido ({options.MinPoolSize}) — não pode ser negativo.");

        if (options.MaxPoolSize < 1)
            failures.Add($"VoxDatabase:MaxPoolSize inválido ({options.MaxPoolSize}) — precisa ser pelo menos 1.");

        if (options.MinPoolSize > options.MaxPoolSize)
            failures.Add($"VoxDatabase:MinPoolSize ({options.MinPoolSize}) não pode ser maior que MaxPoolSize ({options.MaxPoolSize}).");

        if (options.ConnectionLifetimeSeconds < 1)
            failures.Add($"VoxDatabase:ConnectionLifetimeSeconds inválido ({options.ConnectionLifetimeSeconds}) — precisa ser pelo menos 1.");

        if (options.ConnectionTimeoutSeconds < 1)
            failures.Add($"VoxDatabase:ConnectionTimeoutSeconds inválido ({options.ConnectionTimeoutSeconds}) — precisa ser pelo menos 1.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
