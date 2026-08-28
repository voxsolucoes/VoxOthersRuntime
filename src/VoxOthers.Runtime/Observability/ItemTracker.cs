using System.Text.Json;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Pipeline;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Responde "onde parou o item X".
/// </summary>
/// <remarks>
/// <para>
/// É o critério de conclusão desta fase. Hoje, descobrir por que uma gravação
/// não apareceu exige abrir dois sistemas, comparar dois logs sem ligação entre
/// si e, na maior parte das vezes, desistir e pedir o dado de novo à origem.
/// </para>
/// <para>
/// <b>Não há acervo novo.</b> A resposta é montada sobre o que o serviço já
/// grava: o marcador de importado e o arquivo de quarentena. Guardar uma
/// terceira cópia do histórico só para consultá-lo criaria mais uma coisa para
/// encher, expurgar e discordar das outras duas — e o dia em que ela
/// discordasse seria justamente o dia em que alguém estivesse investigando.
/// </para>
/// <para>
/// <b>Custo da consulta.</b> Sem a operação, é um <c>File.Exists</c> por pasta
/// de operação (uma dúzia, na prática) mais uma busca por nome na quarentena. É
/// operação de suporte, feita algumas vezes por dia — não vale indexar.
/// Informando a operação, vira um acesso só.
/// </para>
/// </remarks>
public sealed class ItemTracker
{
    private readonly IOptionsMonitor<DeduplicationOptions> _deduplicacao;
    private readonly IOptionsMonitor<QuarantineOptions> _quarentena;
    private readonly ILogger<ItemTracker> _logger;

    public ItemTracker(
        IOptionsMonitor<DeduplicationOptions> deduplicacao,
        IOptionsMonitor<QuarantineOptions> quarentena,
        ILogger<ItemTracker> logger)
    {
        _deduplicacao = deduplicacao;
        _quarentena = quarentena;
        _logger = logger;
    }

    /// <summary>Procura o item nos dois acervos e monta o histórico dele.</summary>
    /// <param name="uniqueId">Identificador do atendimento na origem.</param>
    /// <param name="operacao">
    /// Operação, quando conhecida. Dispensa a varredura das pastas de operação.
    /// </param>
    public async Task<RastroDoItem> ProcurarAsync(
        string uniqueId, int? operacao, CancellationToken cancellationToken)
    {
        var importado = ProcurarImportado(uniqueId, operacao);
        var recusas = await ProcurarRecusasAsync(uniqueId, cancellationToken);

        // Ordem do mais recente para o mais antigo: quem investiga quer primeiro
        // a última coisa que aconteceu com o item.
        recusas = [.. recusas.OrderByDescending(r => r.Em)];

        var situacao = Concluir(importado, recusas);

        return new RastroDoItem
        {
            UniqueId = uniqueId,
            Situacao = situacao,
            Importado = importado,
            Recusas = recusas,
            OndeProcurar = Explicar(situacao, importado, recusas)
        };
    }

    // ---- Importado ----------------------------------------------------------

    private ImportacaoDoItem? ProcurarImportado(string uniqueId, int? operacao)
    {
        var raiz = _deduplicacao.CurrentValue.Path;

        if (string.IsNullOrWhiteSpace(raiz) || !Directory.Exists(raiz))
        {
            return null;
        }

        if (operacao is { } conhecida)
        {
            return LerMarcador(Path.Combine(raiz, ImportLedger.CaminhoDoMarcador(conhecida, uniqueId)));
        }

        foreach (var pasta in Directory.EnumerateDirectories(raiz, "op-*"))
        {
            if (!int.TryParse(Path.GetFileName(pasta).AsSpan("op-".Length), out var numero))
            {
                continue;
            }

            var achado = LerMarcador(Path.Combine(raiz, ImportLedger.CaminhoDoMarcador(numero, uniqueId)));

            if (achado is not null)
            {
                return achado;
            }
        }

        return null;
    }

    /// <summary>
    /// Lê o marcador, que é um arquivo de <c>chave=valor</c> por linha.
    /// </summary>
    /// <remarks>
    /// O que responde "foi importado?" é o arquivo existir; o conteúdo é o
    /// detalhe que torna a resposta útil. Por isso um conteúdo estranho não
    /// invalida a resposta: devolve-se a importação com os campos que deu para
    /// ler, em vez de dizer que o item não entrou — o que seria falso e mandaria
    /// alguém reimportar uma gravação que já está lá.
    /// </remarks>
    private ImportacaoDoItem? LerMarcador(string caminho)
    {
        if (!File.Exists(caminho))
        {
            return null;
        }

        var campos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var linha in File.ReadAllLines(caminho))
            {
                var corte = linha.IndexOf('=');

                if (corte > 0)
                {
                    campos[linha[..corte]] = linha[(corte + 1)..];
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Here().Warn(ex, "Marcador {Caminho} existe mas não pôde ser lido.", caminho);
        }

        return new ImportacaoDoItem
        {
            Em = Data(campos.GetValueOrDefault("importadoEm")),
            Operacao = int.TryParse(campos.GetValueOrDefault("operacao"), out var op) ? op : null,
            Bilhete = campos.GetValueOrDefault("bilhete"),
            Canal = campos.GetValueOrDefault("canal"),
            Usuario = campos.GetValueOrDefault("usuario"),
            Marcador = caminho
        };
    }

    // ---- Quarentena ---------------------------------------------------------

    private async Task<IReadOnlyList<RecusaDoItem>> ProcurarRecusasAsync(
        string uniqueId, CancellationToken cancellationToken)
    {
        var raiz = _quarentena.CurrentValue.Path;

        if (string.IsNullOrWhiteSpace(raiz) || !Directory.Exists(raiz))
        {
            return [];
        }

        // O nome do arquivo é "<id seguro>-<guid>.json". Procurar pelo prefixo
        // deixa o sistema de arquivos filtrar, em vez de abrir a pasta inteira.
        var padrao = ItemQuarantine.NomeSeguro(uniqueId) + "-*.json";
        var reprocessados = RetentionCleanup.PastaDeReprocessados(raiz);
        var achados = new List<RecusaDoItem>();

        foreach (var arquivo in Directory.EnumerateFiles(raiz, padrao, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            ItemEmQuarentena? registro;

            try
            {
                await using var fluxo = File.OpenRead(arquivo);

                registro = await JsonSerializer.DeserializeAsync<ItemEmQuarentena>(
                    fluxo, ItemQuarantine.Formato, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.Here().Warn(ex, "Arquivo de quarentena {Caminho} não pôde ser lido.", arquivo);
                continue;
            }

            if (registro is null)
            {
                continue;
            }

            // O nome do arquivo é uma versão "segura" do identificador, então
            // dois identificadores diferentes podem cair no mesmo prefixo. Quem
            // decide é o conteúdo.
            if (!string.Equals(registro.Item.UniqueId, uniqueId, StringComparison.Ordinal))
            {
                continue;
            }

            achados.Add(new RecusaDoItem
            {
                Em = registro.QuarantinedAt,
                Tipo = registro.Kind.ToString(),
                Motivo = registro.Reason,
                Lote = registro.BatchId,
                Origem = registro.Source,
                Arquivo = arquivo,
                Reprocessado = arquivo.StartsWith(reprocessados, StringComparison.OrdinalIgnoreCase)
            });
        }

        return achados;
    }

    // ---- Conclusão ----------------------------------------------------------

    /// <summary>
    /// Traduz o que foi encontrado em uma palavra.
    /// </summary>
    /// <remarks>
    /// Estar importado vence tudo, inclusive uma recusa anterior: o item entrou,
    /// e o histórico de tentativas é contexto, não pendência. A distinção entre
    /// recusa esperando e recusa já reenviada é o que separa "alguém precisa
    /// olhar" de "já foi devolvido para a fila".
    /// </remarks>
    private static string Concluir(ImportacaoDoItem? importado, IReadOnlyList<RecusaDoItem> recusas)
    {
        if (importado is not null) return SituacoesDoItem.Importado;
        if (recusas.Any(r => !r.Reprocessado)) return SituacoesDoItem.EmQuarentena;
        if (recusas.Count > 0) return SituacoesDoItem.Reprocessado;

        return SituacoesDoItem.Desconhecido;
    }

    /// <summary>
    /// O próximo passo, em português.
    /// </summary>
    /// <remarks>
    /// A parte mais útil da resposta e a única que não sai dos dados: quem abre
    /// esta consulta quase nunca é quem escreveu o serviço, e uma situação sem
    /// instrução vira uma pergunta no grupo do time de qualquer jeito.
    /// </remarks>
    private static string Explicar(
        string situacao, ImportacaoDoItem? importado, IReadOnlyList<RecusaDoItem> recusas)
        => situacao switch
        {
            SituacoesDoItem.Importado =>
                $"O atendimento entrou no Vox em {Texto(importado!.Em)}, bilhete {importado.Bilhete}. " +
                $"Procure no Vox pelo canal {importado.Canal} e pelo operador {importado.Usuario}, " +
                $"na data do atendimento. Se não aparecer lá, o problema é do lado do Vox " +
                "(importação do bilhete ou permissão de quem procura), não deste serviço.",

            SituacoesDoItem.EmQuarentena =>
                $"O atendimento NÃO entrou. Foi recusado em {Texto(recusas[0].Em)} " +
                $"por: {recusas[0].Motivo}. " +
                (recusas[0].Tipo == nameof(QuarantineKind.Infraestrutura)
                    ? "É falha de ambiente — o dado está bom. Restabeleça o que estava fora do ar " +
                      "e use o reprocessamento da quarentena."
                    : "É problema no dado — reenviar do jeito que está falharia igual. " +
                      "Corrija na origem ou no Builder e mande de novo.") +
                $" O item inteiro está guardado em {recusas[0].Arquivo}.",

            SituacoesDoItem.Reprocessado =>
                $"O atendimento foi recusado, devolvido para a fila em {Texto(recusas[0].Em)} " +
                "e ainda não consta como importado. Se já passou tempo suficiente, ele falhou " +
                "de novo: procure uma recusa mais recente ou olhe o log pelo lote " +
                $"{recusas[0].Lote}.",

            _ =>
                "Não há registro deste atendimento neste serviço. Ou ele ainda está na fila " +
                "(consulte a situação do serviço), ou nunca chegou aqui — nesse caso o rastro " +
                "está no Builder ou na origem, e não neste serviço."
        };

    private static DateTimeOffset? Data(string? valor)
        => DateTimeOffset.TryParse(valor, out var lida) ? lida : null;

    private static string Texto(DateTimeOffset? quando)
        => quando?.ToString("dd/MM/yyyy HH:mm:ss") ?? "data desconhecida";
}

/// <summary>As situações possíveis de um item.</summary>
public static class SituacoesDoItem
{
    public const string Importado = "importado";
    public const string EmQuarentena = "em-quarentena";
    public const string Reprocessado = "reprocessado";
    public const string Desconhecido = "desconhecido";
}

/// <summary>Tudo o que se sabe sobre um atendimento.</summary>
public sealed record RastroDoItem
{
    public required string UniqueId { get; init; }

    /// <summary>Ver <see cref="SituacoesDoItem"/>.</summary>
    public required string Situacao { get; init; }

    /// <summary>O que fazer a seguir, em português.</summary>
    public required string OndeProcurar { get; init; }

    public ImportacaoDoItem? Importado { get; init; }

    /// <summary>Tentativas recusadas, da mais recente para a mais antiga.</summary>
    public IReadOnlyList<RecusaDoItem> Recusas { get; init; } = [];
}

public sealed record ImportacaoDoItem
{
    public DateTimeOffset? Em { get; init; }
    public int? Operacao { get; init; }
    public string? Bilhete { get; init; }
    public string? Canal { get; init; }
    public string? Usuario { get; init; }

    /// <summary>Arquivo que comprova a importação.</summary>
    public required string Marcador { get; init; }
}

public sealed record RecusaDoItem
{
    public required DateTimeOffset Em { get; init; }
    public required string Tipo { get; init; }
    public required string Motivo { get; init; }
    public required string Lote { get; init; }
    public required string Origem { get; init; }
    public required string Arquivo { get; init; }

    /// <summary>Verdadeiro quando o item já foi devolvido para a fila.</summary>
    public required bool Reprocessado { get; init; }
}
