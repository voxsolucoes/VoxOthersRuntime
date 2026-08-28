using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using VoxOthers.Contracts;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>
/// Natureza da recusa. Muda o que a pessoa que for resolver precisa fazer.
/// </summary>
public enum QuarantineKind
{
    /// <summary>
    /// O item, como veio, não dá para importar: mídia que não existe, operador
    /// sem identificação, campo fora do combinado. Resolver é corrigir a origem
    /// ou o Builder.
    /// </summary>
    Dados,

    /// <summary>
    /// O item está bom; o ambiente é que falhou — banco fora do ar, disco cheio,
    /// pasta de rede indisponível. Resolver é restabelecer o ambiente e
    /// reprocessar; não há nada de errado com o dado.
    /// </summary>
    Infraestrutura
}

/// <summary>
/// Guarda em disco o item que não pôde ser importado, junto do motivo.
/// </summary>
/// <remarks>
/// Existe para que uma falha nunca signifique atendimento perdido. Sem isso, um
/// item que falha depois de sair da fila desaparece: não está no Vox, não está
/// mais na entrada e ninguém tem como saber que ele existiu. Com quarentena,
/// existe um arquivo com o item inteiro e o motivo — que dá para investigar e,
/// depois de corrigida a causa, devolver para a entrada.
/// </remarks>
public interface IItemQuarantine
{
    /// <summary>Guarda o item e devolve o caminho do arquivo gravado.</summary>
    Task<string> QuarantineAsync(
        CentralizeEntity entity,
        string batchId,
        string source,
        QuarantineKind kind,
        string reason,
        CancellationToken cancellationToken);
}

public sealed class ItemQuarantine : IItemQuarantine
{
    /// <summary>
    /// Formato do arquivo de quarentena.
    /// </summary>
    /// <remarks>
    /// Visível para o reprocessamento, que lê estes arquivos de volta. Uma
    /// configuração só para os dois lados é o que garante que eles nunca
    /// divirjam — declarar outra igual lá seria esperar que alguém lembrasse de
    /// mudar as duas.
    /// </remarks>
    internal static readonly JsonSerializerOptions Formato = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IOptionsMonitor<QuarantineOptions> _options;
    private readonly ILogger<ItemQuarantine> _logger;

    public ItemQuarantine(IOptionsMonitor<QuarantineOptions> options, ILogger<ItemQuarantine> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<string> QuarantineAsync(
        CentralizeEntity entity,
        string batchId,
        string source,
        QuarantineKind kind,
        string reason,
        CancellationToken cancellationToken)
    {
        // Uma pasta por dia. Quarentena que acumula meses num diretório só fica
        // impossível de abrir, e o problema aparece justamente no dia ruim.
        var pasta = Path.Combine(_options.CurrentValue.Path, $"{DateTime.Now:yyyy-MM-dd}");
        Directory.CreateDirectory(pasta);

        var registro = new ItemEmQuarentena
        {
            QuarantinedAt = DateTimeOffset.Now,
            Kind = kind,
            Reason = reason,
            BatchId = batchId,
            Source = source,
            Item = entity
        };

        var nome = $"{NomeSeguro(entity.UniqueId)}-{Guid.CreateVersion7():n}.json";
        var destino = Path.Combine(pasta, nome);
        var temporario = destino + ".parcial";

        // Escrever e renomear, e não escrever direto: quem for varrer a pasta
        // — pessoa ou script de reprocessamento — nunca encontra um arquivo
        // pela metade.
        await using (var arquivo = File.Create(temporario))
        {
            await JsonSerializer.SerializeAsync(arquivo, registro, Formato, cancellationToken);
        }

        File.Move(temporario, destino, overwrite: false);

        _logger.Here().Warn(
            "Item {UniqueId} em quarentena ({Tipo}): {Motivo}. Arquivo: {Caminho}",
            entity.UniqueId, kind, reason, destino);

        return destino;
    }

    /// <summary>
    /// O identificador vem de sistema de terceiro e pode ter qualquer coisa
    /// dentro. Vira nome de arquivo aqui, então o que não serve é trocado.
    /// </summary>
    /// <remarks>
    /// Visível para a consulta de rastreio, que procura o arquivo pelo nome. A
    /// troca não é reversível — dois identificadores diferentes podem virar o
    /// mesmo nome —, então quem procura confere o conteúdo antes de dar o
    /// arquivo por achado.
    /// </remarks>
    internal static string NomeSeguro(string uniqueId)
    {
        if (string.IsNullOrWhiteSpace(uniqueId)) return "sem-id";

        var proibidos = Path.GetInvalidFileNameChars();
        var limpo = new string([.. uniqueId.Select(c => proibidos.Contains(c) ? '_' : c)]);

        return limpo.Length <= 80 ? limpo : limpo[..80];
    }
}

/// <summary>Conteúdo do arquivo de quarentena.</summary>
public sealed record ItemEmQuarentena
{
    public required DateTimeOffset QuarantinedAt { get; init; }
    public required QuarantineKind Kind { get; init; }
    public required string Reason { get; init; }
    public required string BatchId { get; init; }
    public required string Source { get; init; }

    /// <summary>
    /// O item inteiro, como chegou. É o que permite reprocessar depois de
    /// corrigida a causa, sem precisar pedir o dado de novo à origem.
    /// </summary>
    public required CentralizeEntity Item { get; init; }
}
