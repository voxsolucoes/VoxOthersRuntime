namespace VoxOthers.Contracts;

/// <summary>
/// Resultado da conferência de um registro ou de um lote.
/// </summary>
/// <remarks>
/// Acumula <b>todos</b> os problemas em vez de parar no primeiro. Quem está
/// escrevendo um backend novo no Builder corrige tudo de uma vez, em vez de
/// descobrir um erro por tentativa.
/// </remarks>
public sealed class ContractValidationResult
{
    private static readonly string[] Nenhum = [];

    /// <summary>Resultado sem nenhum problema.</summary>
    public static readonly ContractValidationResult Valid = new(Nenhum);

    private ContractValidationResult(IReadOnlyList<string> errors) => Errors = errors;

    /// <summary>Problemas encontrados, em linguagem direta.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Verdadeiro quando nada impede o processamento.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Cria um resultado a partir de uma lista de problemas.</summary>
    public static ContractValidationResult FromErrors(IReadOnlyList<string> errors)
        => errors.Count == 0 ? Valid : new ContractValidationResult(errors);

    /// <summary>
    /// Junta os problemas em uma única frase, para log ou para o arquivo que
    /// acompanha o registro na quarentena.
    /// </summary>
    public string ToMessage() => string.Join("; ", Errors);

    /// <inheritdoc />
    public override string ToString() => IsValid ? "válido" : ToMessage();
}
