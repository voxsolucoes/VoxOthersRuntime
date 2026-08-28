namespace VoxOthers.Contracts;

/// <summary>
/// Sentido do atendimento.
/// </summary>
/// <remarks>
/// O bilhete do Vox só representa três sentidos (indefinido, entrante e
/// sainte). <see cref="Internal"/> existe aqui porque vários sistemas de
/// origem distinguem a chamada interna, e perder essa informação na
/// normalização impediria relatórios futuros. Na geração do bilhete ela é
/// gravada como indefinida.
/// </remarks>
public enum CallDirection
{
    /// <summary>A origem não informou o sentido.</summary>
    Unknown = 0,

    /// <summary>Entrante — o contato ligou para a empresa.</summary>
    Inbound = 1,

    /// <summary>Sainte — a empresa ligou para o contato.</summary>
    Outbound = 2,

    /// <summary>Interna — entre ramais da própria empresa.</summary>
    Internal = 3
}
