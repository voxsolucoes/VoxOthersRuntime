using System.Security.Cryptography;
using System.Text;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Ingestion;

/// <summary>
/// Confere a chave de acesso das rotas do Runtime.
/// </summary>
/// <remarks>
/// Uma implementação só, usada pelo webhook e pela quarentena. São as duas
/// portas por onde entra dado no Vox, e ter duas conferências parecidas seria a
/// receita para uma delas ficar para trás numa correção.
/// </remarks>
internal static class ApiKeyGuard
{
    /// <summary>Cabeçalho que carrega a chave de acesso da origem.</summary>
    public const string Header = "X-Api-Key";

    /// <summary>
    /// Devolve a origem dona da chave usada, ou nulo.
    /// </summary>
    /// <param name="falha">
    /// Preenchido quando o acesso deve ser recusado. Nulo com origem nula
    /// significa que a exigência de chave está desligada.
    /// </param>
    public static string? Autenticar(
        HttpContext context,
        WebhookIngestionOptions webhook,
        out string? falha)
    {
        falha = null;

        if (!webhook.RequireApiKey)
        {
            return null;
        }

        if (!context.Request.Headers.TryGetValue(Header, out var enviada)
            || string.IsNullOrWhiteSpace(enviada))
        {
            falha = $"Cabeçalho {Header} ausente.";
            return null;
        }

        var chave = enviada.ToString();

        foreach (var (origem, esperada) in webhook.ApiKeys)
        {
            if (ChavesConferem(chave, esperada))
            {
                return origem;
            }
        }

        falha = "Chave de acesso não reconhecida.";
        return null;
    }

    /// <summary>
    /// Compara duas chaves em tempo constante.
    /// </summary>
    /// <remarks>
    /// Comparar com <c>==</c> para na primeira letra diferente, e a diferença
    /// de tempo entre uma chave que erra no primeiro caractere e outra que erra
    /// no último é medível pela rede. Com ela dá para descobrir a chave
    /// caractere a caractere. O custo de evitar isso é uma função de dez
    /// linhas.
    /// </remarks>
    private static bool ChavesConferem(string enviada, string esperada)
    {
        var a = Encoding.UTF8.GetBytes(enviada);
        var b = Encoding.UTF8.GetBytes(esperada);

        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
