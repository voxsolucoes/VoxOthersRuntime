using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Entrega de um item já validado ao destino final.
/// </summary>
/// <remarks>
/// A abstração existe por uma razão concreta, não estética: hoje a entrega é
/// bilhete <c>.GRF</c> em pasta (AD-4), e escrever direto no core do Vox
/// continua em aberto. Ela também é o ponto onde o teste substitui a escrita
/// real sem precisar de disco.
/// </remarks>
public interface IImportSink
{
    /// <summary>
    /// Entrega o item e devolve a referência do que foi gravado — o caminho do
    /// bilhete, no caso do <c>.GRF</c>.
    /// </summary>
    /// <exception cref="Exception">
    /// Falhou a entrega. Quem chama põe o item em quarentena com a mensagem.
    /// </exception>
    Task<string> ProcessAsync(ImportedItemContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Item pronto para entrega: o que veio no contrato mais o que o pipeline
/// resolveu (canal, usuário, login).
/// </summary>
public sealed record ImportedItemContext
{
    /// <summary>O item, como chegou do Builder.</summary>
    public required CentralizeEntity Entity { get; init; }

    /// <summary>Identificador do item. Atalho para <c>Entity.UniqueId</c>.</summary>
    public required string UniqueId { get; init; }

    /// <summary>Operação de destino, usada na chave de deduplicação.</summary>
    public required int OperationId { get; init; }

    /// <summary>Canal alocado para o atendimento.</summary>
    public required int ChannelNumber { get; init; }

    /// <summary>Código do usuário resolvido ou criado na base.</summary>
    public required string UserCodeUsuario { get; init; }

    /// <summary>Nome do operador, como sai no bilhete.</summary>
    public required string UserName { get; init; }

    /// <summary>Código do login na base. Sai no campo <c>CL</c> do bilhete.</summary>
    public required string CodLogin { get; init; }

    /// <summary>
    /// Origem do lote, como sai no campo <c>SC</c>. Vem do envelope, e não do
    /// item: é quem enviou, não o que foi enviado.
    /// </summary>
    public required string Source { get; init; }
}
