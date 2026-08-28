using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Verifica se as pastas de ingestão continuam acessíveis.
/// </summary>
/// <remarks>
/// Caminho de rede que some é uma das causas mais comuns de "parou de importar
/// e ninguém percebeu" no sistema atual. A pasta existe no boot e desaparece
/// três semanas depois, quando alguém mexe numa permissão.
///
/// Por isso a checagem é contínua e não só de inicialização: o monitoramento
/// passa a acusar o problema em vez de esperar a reclamação do cliente.
///
/// Lê via <see cref="IOptionsMonitor{T}"/> para refletir alteração de
/// configuração sem reiniciar o serviço.
/// </remarks>
public sealed class IngestionPathsHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<IngestionOptions> _options;

    public IngestionPathsHealthCheck(IOptionsMonitor<IngestionOptions> options) => _options = options;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var folder = _options.CurrentValue.Folder;

        if (!folder.Enabled)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Entrada por pasta desabilitada."));
        }

        var missing = new List<string>();

        foreach (var path in folder.Paths)
        {
            if (!Directory.Exists(path))
            {
                missing.Add(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(folder.QuarantinePath) && !Directory.Exists(folder.QuarantinePath))
        {
            missing.Add(folder.QuarantinePath);
        }

        if (!string.IsNullOrWhiteSpace(folder.ProcessedPath) && !Directory.Exists(folder.ProcessedPath))
        {
            missing.Add(folder.ProcessedPath);
        }

        return Task.FromResult(missing.Count == 0
            ? HealthCheckResult.Healthy("Todas as pastas de ingestão estão acessíveis.")
            : HealthCheckResult.Unhealthy($"Pastas inacessíveis: {string.Join(", ", missing)}"));
    }
}
