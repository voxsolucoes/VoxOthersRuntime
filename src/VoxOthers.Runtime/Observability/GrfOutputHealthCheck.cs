using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Confere se o destino continua alcançável e gravável.
/// </summary>
/// <remarks>
/// <para>
/// As três pastas do bilhete — trabalho, registro e a raiz de gravação — moram
/// em compartilhamento de rede. É o ponto mais frágil da instalação: some com
/// uma troca de senha de serviço, com uma mudança de permissão ou com o
/// servidor de arquivos reiniciando.
/// </para>
/// <para>
/// <b>Existir não basta, tem de dar para gravar.</b> A falha mais comum não é a
/// pasta sumir — é ela continuar visível e a conta do serviço perder a escrita.
/// Uma verificação por <c>Directory.Exists</c> passaria sorrindo enquanto todo
/// atendimento vai para a quarentena. Por isso a checagem grava um arquivo
/// mínimo e o apaga.
/// </para>
/// <para>
/// A gravação de teste acontece só na pasta de <b>trabalho</b>. Na de registro
/// não pode: o Vox varre aquela pasta e consumiria o arquivo de teste como se
/// fosse bilhete. Na raiz de gravação também não, porque é a árvore de mídia do
/// cliente. Para essas duas, a conferência é de alcance — que já é o que costuma
/// falhar junto.
/// </para>
/// </remarks>
public sealed class GrfOutputHealthCheck : IHealthCheck
{
    private readonly IOptionsMonitor<GrfOptions> _options;

    public GrfOutputHealthCheck(IOptionsMonitor<GrfOptions> options) => _options = options;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var grf = _options.CurrentValue;
        var problemas = new List<string>();

        Alcancavel(grf.RegisterPath, "pasta de registro (Grf:RegisterPath)", problemas);
        Alcancavel(grf.RecordingRoot, "raiz de gravação (Grf:RecordingRoot)", problemas);

        if (Alcancavel(grf.WorkPath, "pasta de trabalho (Grf:WorkPath)", problemas))
        {
            Gravavel(grf.WorkPath, problemas);
        }

        return Task.FromResult(problemas.Count == 0
            ? HealthCheckResult.Healthy("O destino do bilhete está acessível e gravável.")
            : HealthCheckResult.Unhealthy(
                "Nenhum atendimento consegue ser entregue: " + string.Join("; ", problemas)));
    }

    private static bool Alcancavel(string caminho, string descricao, List<string> problemas)
    {
        if (string.IsNullOrWhiteSpace(caminho))
        {
            problemas.Add($"a {descricao} não está configurada");
            return false;
        }

        if (!Directory.Exists(caminho))
        {
            problemas.Add($"a {descricao} não existe ou está inacessível ({caminho})");
            return false;
        }

        return true;
    }

    private static void Gravavel(string caminho, List<string> problemas)
    {
        // Nome único por tentativa: duas instâncias do serviço conferindo a
        // saúde ao mesmo tempo não podem disputar o mesmo arquivo e uma delas
        // acusar problema onde não há.
        var teste = Path.Combine(caminho, $".voxothers-saude-{Guid.CreateVersion7():n}.tmp");

        try
        {
            File.WriteAllText(teste, string.Empty);
        }
        catch (Exception ex)
        {
            problemas.Add($"a pasta de trabalho existe mas não aceita gravação ({ex.Message})");
            return;
        }
        finally
        {
            try
            {
                File.Delete(teste);
            }
            catch
            {
                // Escreveu, que é o que se queria saber. Não conseguir apagar não
                // torna o destino inválido, e é lixo que o expurgo recolhe.
            }
        }
    }
}
