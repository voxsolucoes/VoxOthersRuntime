using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VoxOthers.Runtime.Data;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Confere se a base do Vox responde.
/// </summary>
/// <remarks>
/// <para>
/// Banco fora do ar não faz o serviço cair: o item vira falha de ambiente,
/// espera e vai para a quarentena. É o comportamento certo e tem um efeito
/// colateral ruim — <b>o processo continua de pé, aparentando saúde, enquanto
/// nada entra</b>. Sem esta verificação, o alarme só chega pela reclamação de
/// quem procurou uma gravação.
/// </para>
/// <para>
/// <b>Por que o resultado é guardado por alguns segundos.</b> Este endereço é
/// feito para ser consultado de tempos em tempos por um monitorador. Abrir
/// conexão a cada consulta somaria carga na base do cliente para responder uma
/// pergunta cuja resposta não muda de segundo em segundo. Quinze segundos é
/// curto o bastante para o alarme não atrasar de forma perceptível e longo o
/// bastante para o custo sumir.
/// </para>
/// <para>
/// Fica só no <c>ready</c>, nunca no <c>live</c>: um banco lento reiniciaria em
/// laço um serviço que está perfeitamente saudável e apenas sem ter o que fazer.
/// </para>
/// </remarks>
public sealed class VoxDatabaseHealthCheck : IHealthCheck
{
    /// <summary>Por quanto tempo a última resposta continua valendo.</summary>
    private static readonly TimeSpan Validade = TimeSpan.FromSeconds(15);

    private readonly IServiceScopeFactory _escopos;
    private readonly TimeProvider _tempo;
    private readonly SemaphoreSlim _exclusao = new(1, 1);

    private HealthCheckResult _ultima;
    private DateTimeOffset _lidaEm = DateTimeOffset.MinValue;

    public VoxDatabaseHealthCheck(IServiceScopeFactory escopos, TimeProvider tempo)
    {
        _escopos = escopos;
        _tempo = tempo;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var agora = _tempo.GetUtcNow();

        if (agora - _lidaEm < Validade)
        {
            return _ultima;
        }

        await _exclusao.WaitAsync(cancellationToken);

        try
        {
            // Confere de novo: outra consulta pode ter chegado primeiro enquanto
            // esta esperava, e não há motivo para bater na base duas vezes.
            if (_tempo.GetUtcNow() - _lidaEm < Validade)
            {
                return _ultima;
            }

            _ultima = await ConsultarAsync(cancellationToken);
            _lidaEm = _tempo.GetUtcNow();

            return _ultima;
        }
        finally
        {
            _exclusao.Release();
        }
    }

    private async Task<HealthCheckResult> ConsultarAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var escopo = _escopos.CreateScope();

            var banco = escopo.ServiceProvider.GetRequiredService<VoxDbContext>();

            return await banco.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("A base do Vox respondeu.")
                : HealthCheckResult.Unhealthy(
                    "A base do Vox não respondeu. Nenhum atendimento vai entrar enquanto isso durar; " +
                    "eles ficam na quarentena como falha de ambiente e podem ser reprocessados depois.");
        }
        catch (Exception ex)
        {
            // A mensagem da exceção entra no resultado porque é ela que diz se o
            // problema é rede, credencial ou o serviço do banco parado — e o
            // endereço de saúde é o primeiro lugar em que se olha.
            return HealthCheckResult.Unhealthy(
                "Erro ao falar com a base do Vox: " + ex.Message, ex);
        }
    }
}
