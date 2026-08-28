using VoxOthers.Contracts;
using VoxOthers.Runtime.Registration;
using VoxOthers.Runtime.Sinks;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>
/// Validação de negócio — estende a validação de contrato com regras que
/// dependem do estado do sistema (item já importado, operador cadastrado).
/// </summary>
public interface IValidationPipeline
{
    /// <summary>
    /// Valida um item de ponta a ponta: deduplicação e cadastro do operador.
    /// Retorna o resultado e, se bem-sucedido, o contexto pronto para o sink.
    /// </summary>
    /// <param name="source">
    /// Quem enviou o lote. Vem do envelope e não do item, e sai no campo
    /// <c>SC</c> do bilhete.
    /// </param>
    Task<ValidationResult> ValidateAsync(
        CentralizeEntity entity,
        string source,
        CancellationToken cancellationToken);
}

public record ValidationResult
{
    public bool IsValid { get; init; }
    public List<string> Errors { get; init; } = [];
    public ImportedItemContext? Context { get; init; }

    /// <summary>
    /// O item já tinha sido importado.
    /// </summary>
    /// <remarks>
    /// Separado das demais recusas porque não é falha: é a deduplicação
    /// funcionando. Mandar duplicata para a quarentena encheria a pasta de
    /// itens que estão certos e afogaria os que precisam de atenção.
    /// </remarks>
    public bool IsDuplicate { get; init; }

    /// <summary>
    /// Natureza da recusa, quando houve. Decide o que a quarentena registra.
    /// </summary>
    public QuarantineKind FailureKind { get; init; } = QuarantineKind.Infraestrutura;
}

public class ValidationPipeline : IValidationPipeline
{
    private readonly IImportLedger _ledger;
    private readonly IVoxRegistration _registration;
    private readonly ILogger<ValidationPipeline> _logger;

    public ValidationPipeline(
        IImportLedger ledger,
        IVoxRegistration registration,
        ILogger<ValidationPipeline> logger)
    {
        _ledger = ledger;
        _registration = registration;
        _logger = logger;
    }

    public async Task<ValidationResult> ValidateAsync(
        CentralizeEntity entity,
        string source,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        // Conteúdo primeiro: item que não tem o que importar não deve deixar
        // rastro de cadastro na base do Vox.
        try
        {
            ContentValidation.Conferir(entity);
        }
        catch (ItemRejectedException ex)
        {
            _logger.Here().Warn("Item {UniqueId} recusado: {Motivo}", entity.UniqueId, ex.Message);
            errors.Add(ex.Message);

            return new ValidationResult
            {
                IsValid = false,
                Errors = errors,
                FailureKind = QuarantineKind.Dados
            };
        }

        // O contrato traz dois identificadores e eles NÃO são intercambiáveis:
        // ServerId diz em qual instalação do Vox o operador e o canal vivem, e
        // OperationId diz a que operação o atendimento pertence. O cadastro
        // precisa dos dois; a deduplicação, só da operação.

        if (await _ledger.JaImportadoAsync(entity.OperationId, entity.UniqueId, cancellationToken))
        {
            _logger.Here().Warn("Item já importado: op={OperationId} id={UniqueId}",
                entity.OperationId, entity.UniqueId);
            errors.Add($"Item duplicado: {entity.UniqueId}");
            return new ValidationResult { IsValid = false, IsDuplicate = true, Errors = errors };
        }

        RegistrationResult cadastro;
        try
        {
            cadastro = await _registration.EnsureAsync(
                entity.ServerId,
                entity.OperationId,
                entity.AgentLogin,
                entity.AgentName,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Here().Error(ex,
                "Falha ao cadastrar o operador do item {UniqueId} (login={Login} nome={Nome})",
                entity.UniqueId, entity.AgentLogin, entity.AgentName);
            errors.Add($"Falha no cadastro do operador: {ex.Message}");

            return new ValidationResult
            {
                IsValid = false,
                Errors = errors,
                FailureKind = Classificar(ex)
            };
        }

        var context = new ImportedItemContext
        {
            Entity = entity,
            UniqueId = entity.UniqueId,
            OperationId = entity.OperationId,
            ChannelNumber = cadastro.ChannelNumber,
            UserCodeUsuario = cadastro.CodUsuario,
            UserName = cadastro.UserName,
            CodLogin = cadastro.CodLogin.ToString(),
            Source = source
        };

        _logger.Here().Debug("Validação bem-sucedida: op={OperationId} id={UniqueId} user={User} ch={Channel}",
            entity.OperationId, entity.UniqueId, cadastro.UserName, cadastro.ChannelNumber);

        return new ValidationResult { IsValid = true, Context = context };
    }

    /// <summary>
    /// Decide se a culpa é do dado ou do ambiente.
    /// </summary>
    /// <remarks>
    /// Uma regra só, e ela erra para o lado seguro: tudo o que não for uma
    /// recusa explícita do item conta como falha de ambiente. Um item bom
    /// marcado como problema de ambiente custa um reprocessamento; um item ruim
    /// marcado como falha passageira volta para sempre.
    /// </remarks>
    internal static QuarantineKind Classificar(Exception ex) =>
        ex is ItemRejectedException ? QuarantineKind.Dados : QuarantineKind.Infraestrutura;
}
