using Vox.RegBDLib;
using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Traduz um item já validado no objeto de bilhete da <c>RegBDLib</c>.
/// </summary>
/// <remarks>
/// <para>
/// Separado do sink que grava em disco por um motivo prático: assim o
/// mapeamento pode ser conferido em teste sem tocar no sistema de arquivos, e o
/// bilhete gerado pode ser comparado byte a byte com um bilhete real do sistema
/// atual.
/// </para>
/// <para>
/// <b>Regra que vale para todos os campos:</b> só preencher o que o sistema
/// atual preenche. Campo não atribuído sai vazio no bilhete — e é assim que os
/// bilhetes de produção estão hoje (<c>IC:</c>, <c>GM:</c>, <c>AN:</c> e
/// vários outros aparecem vazios). Preencher um campo "porque temos o dado"
/// mudaria bytes que o importador já lê há anos.
/// </para>
/// </remarks>
public sealed class GrfTicketFactory
{
    /// <summary>
    /// Monta o bilhete de encerramento de gravação a partir do item.
    /// </summary>
    public CRegBD261FinishTkt Create(GrfTicketInput input)
    {
        var e = input.Entity;

        var tkt = new CRegBD261FinishTkt
        {
            // Identificação do destino
            Server = input.ServerName,
            Channel = input.ChannelNumber,
            Ramal = input.ChannelNumber.ToString(),

            // Mídia
            RelativePath = input.RelativePath,
            FileName = input.MediaFileName,

            // Tempo. A RegBDLib formata como yyyyMMddHHmmss; o horário vai no
            // fuso do próprio atendimento, que é como o Vox interpreta.
            StartTime = e.StartedAt.LocalDateTime,
            FinishTime = e.EndedAt.LocalDateTime,

            // Chamada
            ANI = e.Ani ?? string.Empty,
            Direction = Traduzir(e.Direction),
            UserData = UserDataBuilder.Build(e),

            // Operador
            Operator = input.OperatorName,
            CodLogin = input.CodLogin,

            // Origem e características da gravação. Constantes porque é o que o
            // sistema atual grava: gravação completa, com cabeçalho, codec 49.
            Source = input.Source,
            Full_Recording = true,
            HasHeader = true,
            Codec = 49
        };

        // Só preenche se houver. Nos bilhetes de produção este campo aparece
        // ora com valor, ora vazio — depende da origem ter identificado o
        // contato ou não.
        if (!string.IsNullOrWhiteSpace(e.ContactName))
        {
            tkt.ContactIdentification = e.ContactName;
        }

        // OT (OPERATION_ID_TKT) fica vazio de propósito. O campo aparece
        // preenchido em alguns bilhetes antigos, mas a equipe confirmou que o
        // importador não o usa: o OperationId do contrato serve para o cadastro
        // do usuário na base, junto do ServerId, e não para rotear o bilhete.
        // Preencher seria escrever um dado que ninguém lê.

        return tkt;
    }

    /// <summary>
    /// Converte a direção do contrato para a enumeração da <c>RegBDLib</c>.
    /// </summary>
    private static enumCallDirection Traduzir(CallDirection direcao) => direcao switch
    {
        CallDirection.Inbound => enumCallDirection.callDirectionIncoming,
        CallDirection.Outbound => enumCallDirection.callDirectionOutgoing,
        _ => enumCallDirection.callDirectionIndef
    };
}

/// <summary>
/// Tudo o que o bilhete precisa e que não vem do contrato — resolvido antes
/// pelo pipeline (canal, login) ou vindo de configuração (nome do servidor).
/// </summary>
public sealed record GrfTicketInput
{
    public required CentralizeEntity Entity { get; init; }

    /// <summary>Nome do servidor Vox, como aparece no campo <c>SR</c>.</summary>
    public required string ServerName { get; init; }

    /// <summary>Canal/ramal alocado para o atendimento.</summary>
    public required int ChannelNumber { get; init; }

    /// <summary>Caminho relativo da mídia, no formato <c>aaaa\MM\dd\canal</c>.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Nome do arquivo de mídia, com extensão.</summary>
    public required string MediaFileName { get; init; }

    /// <summary>Nome do operador como deve aparecer no bilhete.</summary>
    public required string OperatorName { get; init; }

    /// <summary>Código do login resolvido na base.</summary>
    public required string CodLogin { get; init; }

    /// <summary>Origem, como aparece no campo <c>SC</c>.</summary>
    public required string Source { get; init; }
}
