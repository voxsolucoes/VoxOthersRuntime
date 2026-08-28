using Microsoft.Extensions.Options;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Validações do bloco Ingestion que dependem de mais de um campo.
/// </summary>
/// <remarks>
/// Existe para transformar erro silencioso em erro de boot. O caso mais grave
/// é subir o serviço com as duas entradas desligadas: ele fica no ar, saudável,
/// consumindo recurso e sem importar nada — exatamente o tipo de falha que
/// ninguém percebe até alguém reclamar da gravação que sumiu.
/// </remarks>
public sealed class IngestionOptionsValidator : IValidateOptions<IngestionOptions>
{
    public ValidateOptionsResult Validate(string? name, IngestionOptions options)
    {
        var failures = new List<string>();

        if (!options.Folder.Enabled && !options.Webhook.Enabled)
        {
            failures.Add(
                "Nenhuma forma de entrada está habilitada. Ligue Ingestion:Folder:Enabled " +
                "ou Ingestion:Webhook:Enabled — sem isso o serviço sobe mas não importa nada.");
        }

        if (options.Folder.Enabled)
        {
            ValidateFolder(options.Folder, failures);
        }

        if (options.Webhook.Enabled)
        {
            ValidateWebhook(options.Webhook, failures);
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateFolder(FolderIngestionOptions folder, List<string> failures)
    {
        if (folder.Paths.Count == 0)
        {
            failures.Add("Ingestion:Folder:Paths não pode ficar vazio quando a entrada por pasta está habilitada.");
        }
        else if (folder.Paths.Any(string.IsNullOrWhiteSpace))
        {
            failures.Add("Ingestion:Folder:Paths contém caminho em branco.");
        }

        var obrigatorias = new (string Chave, string Valor)[]
        {
            ("QuarantinePath", folder.QuarantinePath),
            ("ProcessedPath", folder.ProcessedPath),
            ("WorkingPath", folder.WorkingPath)
        };

        foreach (var (chave, valor) in obrigatorias)
        {
            if (string.IsNullOrWhiteSpace(valor))
            {
                failures.Add($"Ingestion:Folder:{chave} é obrigatório quando a entrada por pasta está habilitada.");
            }
        }

        if (string.IsNullOrWhiteSpace(folder.FilePattern))
        {
            failures.Add("Ingestion:Folder:FilePattern não pode ficar em branco.");
        }

        // Ler e escrever na mesma pasta faria o serviço reprocessar
        // eternamente o que acabou de concluir.
        foreach (var path in folder.Paths.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            foreach (var (chave, valor) in obrigatorias)
            {
                if (PathsAreEquivalent(path, valor))
                {
                    failures.Add($"Ingestion:Folder:{chave} ('{path}') não pode ser igual a uma pasta de entrada.");
                }
            }
        }

        // Trabalho, concluído e quarentena também precisam ser distintos entre
        // si: com dois deles apontando para o mesmo lugar, um arquivo em
        // quarentena voltaria como se fosse trabalho interrompido, num laço.
        for (var i = 0; i < obrigatorias.Length; i++)
        {
            for (var j = i + 1; j < obrigatorias.Length; j++)
            {
                if (PathsAreEquivalent(obrigatorias[i].Valor, obrigatorias[j].Valor))
                {
                    failures.Add(
                        $"Ingestion:Folder:{obrigatorias[i].Chave} e Ingestion:Folder:{obrigatorias[j].Chave} " +
                        "apontam para a mesma pasta; elas precisam ser diferentes.");
                }
            }
        }
    }

    private static void ValidateWebhook(WebhookIngestionOptions webhook, List<string> failures)
    {
        if (!webhook.Path.StartsWith('/'))
        {
            failures.Add($"Ingestion:Webhook:Path deve começar com '/'. Valor atual: '{webhook.Path}'.");
        }

        if (webhook.RequireApiKey && webhook.ApiKeys.Count == 0)
        {
            failures.Add(
                "Ingestion:Webhook:ApiKeys está vazio com Ingestion:Webhook:RequireApiKey ligado. " +
                "Cadastre ao menos uma origem, ou desligue RequireApiKey conscientemente " +
                "(sem chave, qualquer um que alcance a porta injeta gravação no Vox).");
        }

        foreach (var (origem, chave) in webhook.ApiKeys)
        {
            if (string.IsNullOrWhiteSpace(origem))
            {
                failures.Add("Ingestion:Webhook:ApiKeys contém origem em branco.");
            }

            if (string.IsNullOrWhiteSpace(chave))
            {
                failures.Add($"Ingestion:Webhook:ApiKeys: a chave da origem '{origem}' está em branco.");
            }
        }
    }

    private static bool PathsAreEquivalent(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        return string.Equals(
            Path.TrimEndingDirectorySeparator(left.Trim()),
            Path.TrimEndingDirectorySeparator(right.Trim()),
            StringComparison.OrdinalIgnoreCase);
    }
}
