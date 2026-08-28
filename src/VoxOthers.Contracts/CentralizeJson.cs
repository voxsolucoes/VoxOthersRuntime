using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoxOthers.Contracts;

/// <summary>
/// Forma oficial de serializar e ler o contrato.
/// </summary>
/// <remarks>
/// <para>
/// Existe para que Builder e Runtime não precisem combinar convenções por
/// documentação. Quem monta o lote e quem lê usam este mesmo objeto, e o
/// formato deixa de ser um acordo verbal.
/// </para>
/// <para>
/// Duas escolhas merecem explicação:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <b>Enumerações como texto.</b> Gravar <c>"Inbound"</c> em vez de
///     <c>1</c> custa alguns bytes e resolve dois problemas: o arquivo na
///     pasta monitorada pode ser lido por uma pessoa durante um incidente, e
///     inserir um valor novo no meio da enumeração deixa de reinterpretar
///     silenciosamente lotes antigos.
///     </description>
///   </item>
///   <item>
///     <description>
///     <b>Campo desconhecido derruba a leitura.</b> Um lote com campo que o
///     Runtime não conhece quase sempre significa Builder mais novo falando
///     com Runtime mais velho. Falhar alto, no momento da leitura, é melhor
///     do que importar pela metade e descobrir semanas depois.
///     </description>
///   </item>
/// </list>
/// </remarks>
public static class CentralizeJson
{
    /// <summary>Configuração oficial de leitura e escrita do contrato.</summary>
    public static JsonSerializerOptions Options { get; } = Criar(indentado: false);

    /// <summary>
    /// Mesma configuração, com recuo. Para gerar exemplo, anexo de
    /// quarentena e arquivo destinado a leitura humana.
    /// </summary>
    public static JsonSerializerOptions ReadableOptions { get; } = Criar(indentado: true);

    /// <summary>Lê um lote a partir de JSON.</summary>
    /// <exception cref="JsonException">O conteúdo não corresponde ao contrato.</exception>
    public static CentralizeBatch DeserializeBatch(string json)
    {
        var lote = JsonSerializer.Deserialize<CentralizeBatch>(json, Options)
                   ?? throw new JsonException("O conteúdo do lote é nulo.");
        CompletarUniqueIds(lote);
        return lote;
    }

    /// <summary>
    /// Lê um lote direto de um fluxo, sem materializar o conteúdo inteiro em
    /// memória.
    /// </summary>
    /// <remarks>
    /// É a forma usada tanto pela pasta monitorada quanto pelo webhook. Ler
    /// para uma string primeiro custaria o dobro de memória e colocaria um
    /// arquivo de lote grande no monte de objetos grandes — exatamente o que a
    /// meta de consumo do projeto quer evitar.
    /// </remarks>
    /// <exception cref="JsonException">O conteúdo não corresponde ao contrato.</exception>
    public static async ValueTask<CentralizeBatch> DeserializeBatchAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var lote = await JsonSerializer.DeserializeAsync<CentralizeBatch>(stream, Options, cancellationToken)
                       .ConfigureAwait(false)
                   ?? throw new JsonException("O conteúdo do lote é nulo.");
        CompletarUniqueIds(lote);
        return lote;
    }

    /// <summary>Grava um lote como JSON.</summary>
    public static string Serialize(CentralizeBatch batch, bool readable = false)
        => JsonSerializer.Serialize(batch, readable ? ReadableOptions : Options);

    /// <summary>
    /// Garante o identificador de cada registro do lote. Item sem
    /// <see cref="CentralizeEntity.UniqueId"/> recebe um GUID na leitura, de
    /// modo que nenhum atendimento entre na fila sem identificador.
    /// </summary>
    private static void CompletarUniqueIds(CentralizeBatch lote)
    {
        if (lote.Items is null)
        {
            return;
        }

        foreach (var item in lote.Items)
        {
            item?.EnsureUniqueId();
        }
    }

    private static JsonSerializerOptions Criar(bool indentado)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = indentado
        };

        options.Converters.Add(new JsonStringEnumConverter());

        // Congela a configuração para que ninguém a altere em tempo de execução
        // e o serializador possa guardar o plano de cada tipo. O argumento pede
        // o resolvedor padrão por reflexão: sem ele o congelamento é recusado.
        options.MakeReadOnly(populateMissingResolver: true);

        return options;
    }
}
