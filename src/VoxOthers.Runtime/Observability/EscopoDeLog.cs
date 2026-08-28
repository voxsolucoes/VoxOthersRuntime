using System.Collections;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Escopo de log que sai legível em texto e continua estruturado para quem lê
/// campo a campo.
/// </summary>
/// <remarks>
/// <para>
/// Existe por causa de um defeito encontrado na homologação: com
/// <c>Dictionary&lt;string, object&gt;</c> como escopo, o log saía assim —
/// </para>
/// <code>
/// info: ...ProcessingWorkerService[0] => System.Collections.Generic.Dictionary`2[System.String,System.Object] Item importado: ...
/// </code>
/// <para>
/// O formatador de console do .NET não percorre os pares do escopo: ele chama
/// <c>ToString()</c> no objeto e imprime o que vier. Como dicionário não
/// sobrescreve <c>ToString()</c>, o resultado é o nome do tipo. O escopo estava
/// lá, o custo de mantê-lo estava sendo pago, e a informação que ele carregava
/// — BatchId, Origem, Fonte, UniqueId — não chegava a lugar nenhum.
/// </para>
/// <para>
/// A correção é sobrescrever <c>ToString()</c>. Continuar implementando
/// <see cref="IReadOnlyList{T}"/> de pares importa tanto quanto: é assim que
/// provedores estruturados (console em JSON, Seq, OpenTelemetry) enxergam
/// <c>BatchId</c> como um campo pesquisável em vez de um pedaço de texto. Sem
/// isso, a correção do console quebraria a busca por identificador, que é
/// justamente o que a fase de observabilidade entregou.
/// </para>
/// </remarks>
public sealed class EscopoDeLog : IReadOnlyList<KeyValuePair<string, object>>
{
    private readonly KeyValuePair<string, object>[] _campos;
    private readonly string _texto;

    private EscopoDeLog(KeyValuePair<string, object>[] campos)
    {
        _campos = campos;
        _texto = string.Join(", ", campos.Select(campo => $"{campo.Key}={campo.Value}"));
    }

    /// <summary>Escopo de um campo só.</summary>
    public static EscopoDeLog De(string chave, object valor)
        => new([new KeyValuePair<string, object>(chave, valor)]);

    /// <summary>Escopo de dois campos, na ordem em que foram informados.</summary>
    public static EscopoDeLog De(string chave1, object valor1, string chave2, object valor2)
        => new([
            new KeyValuePair<string, object>(chave1, valor1),
            new KeyValuePair<string, object>(chave2, valor2)
        ]);

    /// <summary>Escopo de três campos, na ordem em que foram informados.</summary>
    /// <remarks>
    /// A ordem é preservada de propósito: quem lê o log espera ver o
    /// identificador do lote antes da origem, e não a ordem de espalhamento de
    /// uma tabela de dispersão.
    /// </remarks>
    public static EscopoDeLog De(
        string chave1, object valor1,
        string chave2, object valor2,
        string chave3, object valor3)
        => new([
            new KeyValuePair<string, object>(chave1, valor1),
            new KeyValuePair<string, object>(chave2, valor2),
            new KeyValuePair<string, object>(chave3, valor3)
        ]);

    public int Count => _campos.Length;

    public KeyValuePair<string, object> this[int index] => _campos[index];

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        => ((IEnumerable<KeyValuePair<string, object>>)_campos).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _campos.GetEnumerator();

    /// <summary>O que aparece no log em texto.</summary>
    public override string ToString() => _texto;
}
