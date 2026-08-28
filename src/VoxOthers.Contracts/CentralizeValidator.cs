using System.Globalization;
using System.Text;

namespace VoxOthers.Contracts;

/// <summary>
/// Confere se um registro ou lote atende ao contrato.
/// </summary>
/// <remarks>
/// <para>
/// Fica no projeto de contrato, e não no Runtime, de propósito: quem escreve
/// um backend no Builder consegue validar o que produziu <b>antes</b> de
/// entregar, usando exatamente a mesma regra que o Runtime aplicará. Uma só
/// definição de "registro válido", nos dois lados.
/// </para>
/// <para>
/// A conferência cobre o que quebraria mais adiante de forma difícil de
/// diagnosticar: nome de arquivo inválido, marcação de dados livres
/// malformada, mídia ausente. Ela não tenta adivinhar se o conteúdo é
/// verdadeiro — isso é responsabilidade da origem.
/// </para>
/// </remarks>
public static class CentralizeValidator
{
    /// <summary>
    /// Tamanho máximo do identificador. Ele compõe o nome do arquivo do
    /// bilhete, junto de canal e data; o limite mantém o caminho final
    /// dentro do que o sistema de arquivos aceita.
    /// </summary>
    public const int UniqueIdMaxLength = 100;

    /// <summary>
    /// Caracteres que não podem aparecer no identificador.
    /// </summary>
    /// <remarks>
    /// A lista é fixa em vez de usar <see cref="Path.GetInvalidFileNameChars"/>
    /// porque aquela varia com o sistema operacional: no Linux ela devolve
    /// apenas dois caracteres. O Builder pode rodar em Linux e o servidor Vox
    /// é Windows — deixar a regra depender de onde a conferência acontece
    /// produziria lote aprovado na origem e quebrado no destino.
    /// </remarks>
    private static readonly char[] CaracteresProibidosNoId =
        ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>Confere um registro isolado.</summary>
    public static ContractValidationResult Validate(CentralizeEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        var erros = new List<string>();
        ValidateInto(entity, prefixo: string.Empty, erros);
        return ContractValidationResult.FromErrors(erros);
    }

    /// <summary>
    /// Confere apenas o envelope do lote — versão, origem e duplicidade entre
    /// os itens —, sem entrar no conteúdo de cada registro.
    /// </summary>
    /// <remarks>
    /// Separado da conferência item a item porque a decisão é diferente:
    /// envelope inválido invalida o lote inteiro, enquanto um item ruim é
    /// mandado para quarentena sem impedir os demais.
    /// </remarks>
    public static ContractValidationResult ValidateEnvelope(CentralizeBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var erros = new List<string>();

        if (!SchemaVersions.IsSupported(batch.SchemaVersion))
        {
            erros.Add(
                $"SchemaVersion {batch.SchemaVersion} não é reconhecida. " +
                $"Versões aceitas: {string.Join(", ", SchemaVersions.Supported)}.");
        }

        if (string.IsNullOrWhiteSpace(batch.Source))
        {
            erros.Add("Source é obrigatório: identifica qual origem gerou o lote.");
        }

        if (batch.Items is null || batch.Items.Count == 0)
        {
            erros.Add("O lote não contém nenhum atendimento.");
        }
        else
        {
            AddDuplicateIdErrors(batch.Items, erros);
        }

        return ContractValidationResult.FromErrors(erros);
    }

    /// <summary>
    /// Confere o lote inteiro: envelope e cada registro. Os problemas de item
    /// vêm identificados pela posição, como <c>Items[3]</c>.
    /// </summary>
    public static ContractValidationResult Validate(CentralizeBatch batch)
    {
        var envelope = ValidateEnvelope(batch);

        var erros = new List<string>(envelope.Errors);

        if (batch.Items is not null)
        {
            for (var i = 0; i < batch.Items.Count; i++)
            {
                var item = batch.Items[i];
                if (item is null)
                {
                    erros.Add($"Items[{i}]: registro nulo.");
                    continue;
                }

                ValidateInto(item, $"Items[{i}]: ", erros);
            }
        }

        return ContractValidationResult.FromErrors(erros);
    }

    /// <summary>
    /// Ajusta a chave de um campo livre para que ela sirva como nome de
    /// marcação nos dados livres do bilhete.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A alternativa seria recusar a chave e mandar o registro para
    /// quarentena. Não compensa: um espaço no nome de um campo não invalida
    /// o atendimento, e perder a gravação por causa disso seria um prejuízo
    /// muito maior do que gravar o campo como <c>NUMERO_PROTOCOLO</c> em vez
    /// de <c>Numero Protocolo</c>. O Runtime resolve, e o backend não precisa
    /// conhecer a regra.
    /// </para>
    /// <para>O ajuste, em ordem:</para>
    /// <list type="number">
    ///   <item><description>acento é retirado (<c>ç</c> vira <c>c</c>);</description></item>
    ///   <item><description>espaço e ponto viram sublinhado;</description></item>
    ///   <item><description>o que não for letra, número, <c>_</c> ou <c>-</c> é descartado;</description></item>
    ///   <item><description>chave que comece por número ou <c>-</c> ganha um <c>_</c> na frente, porque nome de marcação não pode começar assim.</description></item>
    /// </list>
    /// <para>
    /// Devolve vazio quando não sobra nada aproveitável — só nesse caso a
    /// chave é recusada.
    /// </para>
    /// </remarks>
    public static string NormalizeExtensionKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var semAcento = key.Trim().Normalize(NormalizationForm.FormD);
        var resultado = new StringBuilder(semAcento.Length + 1);

        foreach (var c in semAcento)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c is ' ' or '.' or '_')
            {
                resultado.Append('_');
            }
            else if (char.IsAsciiLetterOrDigit(c) || c == '-')
            {
                resultado.Append(c);
            }
        }

        if (resultado.Length == 0)
        {
            return string.Empty;
        }

        if (!char.IsAsciiLetter(resultado[0]) && resultado[0] != '_')
        {
            resultado.Insert(0, '_');
        }

        return resultado.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Diz se a chave de um campo livre pode ser aproveitada — ou seja, se o
    /// ajuste de <see cref="NormalizeExtensionKey"/> deixa algo utilizável.
    /// </summary>
    public static bool IsValidExtensionKey(string? key)
        => NormalizeExtensionKey(key).Length > 0;

    private static void ValidateInto(CentralizeEntity entity, string prefixo, List<string> erros)
    {
        ValidateUniqueId(entity.UniqueId, prefixo, erros);

        if (string.IsNullOrWhiteSpace(entity.AgentLogin) && string.IsNullOrWhiteSpace(entity.AgentName))
        {
            erros.Add(
                $"{prefixo}Informe AgentLogin ou AgentName — sem um deles não há como " +
                "descobrir qual operador atendeu.");
        }

        if (entity.StartedAt == default)
        {
            erros.Add($"{prefixo}StartedAt é obrigatório.");
        }

        if (entity.DurationSeconds < 0)
        {
            erros.Add($"{prefixo}DurationSeconds não pode ser negativo (recebido: {entity.DurationSeconds}).");
        }

        if (entity.ServerId <= 0)
        {
            erros.Add(
                $"{prefixo}ServerId é obrigatório e deve ser maior que zero " +
                $"(recebido: {entity.ServerId}). É ele que diz em qual servidor Vox " +
                "o atendimento deve entrar.");
        }

        if (entity.OperationId <= 0)
        {
            erros.Add(
                $"{prefixo}OperationId é obrigatório e deve ser maior que zero " +
                $"(recebido: {entity.OperationId}).");
        }

        ValidateMedia(entity, prefixo, erros);
        ValidateAttachments(entity, prefixo, erros);
        ValidateExtensions(entity, prefixo, erros);
    }

    private static void ValidateUniqueId(string uniqueId, string prefixo, List<string> erros)
    {
        if (string.IsNullOrWhiteSpace(uniqueId))
        {
            erros.Add($"{prefixo}UniqueId é obrigatório.");
            return;
        }

        if (uniqueId.Length > UniqueIdMaxLength)
        {
            erros.Add(
                $"{prefixo}UniqueId tem {uniqueId.Length} caracteres, acima do limite " +
                $"de {UniqueIdMaxLength}.");
        }

        var invalidos = uniqueId
            .Where(c => char.IsControl(c) || Array.IndexOf(CaracteresProibidosNoId, c) >= 0)
            .Distinct()
            .ToArray();

        if (invalidos.Length > 0)
        {
            erros.Add(
                $"{prefixo}UniqueId contém caractere que não pode ir para nome de arquivo: " +
                $"{string.Join(" ", invalidos.Select(c => char.IsControl(c) ? "<controle>" : $"'{c}'"))}.");
        }
    }

    private static void ValidateMedia(CentralizeEntity entity, string prefixo, List<string> erros)
    {
        var temMidia = !string.IsNullOrWhiteSpace(entity.MediaPath);

        switch (entity.Kind)
        {
            case MediaKind.Call when !temMidia:
                erros.Add($"{prefixo}MediaPath é obrigatório em atendimento de voz.");
                break;

            case MediaKind.Chat when !temMidia && (entity.Messages is null || entity.Messages.Count == 0):
                erros.Add(
                    $"{prefixo}Atendimento de texto precisa de Messages ou de MediaPath — " +
                    "do contrário não há conteúdo para guardar.");
                break;
        }

        if (entity.Messages is null)
        {
            return;
        }

        for (var i = 0; i < entity.Messages.Count; i++)
        {
            var mensagem = entity.Messages[i];

            // Mensagem que carrega arquivo pode vir sem texto — no chat é comum
            // mandar só o documento. Sem texto e sem arquivo, porém, não há o
            // que mostrar, e a mensagem só ocuparia espaço na conversa.
            if (string.IsNullOrEmpty(mensagem?.Text) && mensagem?.Attachment is null)
            {
                erros.Add($"{prefixo}Messages[{i}] está sem texto e sem arquivo.");
            }

            if (mensagem?.Attachment is not null
                && string.IsNullOrWhiteSpace(mensagem.Attachment.Path))
            {
                erros.Add($"{prefixo}Messages[{i}].Attachment.Path é obrigatório.");
            }
        }
    }

    private static void ValidateAttachments(CentralizeEntity entity, string prefixo, List<string> erros)
    {
        if (entity.Attachments is null)
        {
            return;
        }

        for (var i = 0; i < entity.Attachments.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(entity.Attachments[i]?.Path))
            {
                erros.Add($"{prefixo}Attachments[{i}].Path é obrigatório.");
            }
        }
    }

    private static void ValidateExtensions(CentralizeEntity entity, string prefixo, List<string> erros)
    {
        if (entity.Extensions is null)
        {
            return;
        }

        var normalizadas = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var chave in entity.Extensions.Keys)
        {
            var normalizada = NormalizeExtensionKey(chave);

            if (normalizada.Length == 0)
            {
                erros.Add(
                    $"{prefixo}Extensions: a chave \"{chave}\" não sobrou nada aproveitável " +
                    "depois do ajuste. Use letras, números, '_' e '-'.");
                continue;
            }

            // Duas chaves diferentes que viram a mesma depois do ajuste
            // ("Nº Protocolo" e "N Protocolo") gravariam uma por cima da
            // outra no bilhete, e o campo perdido não apareceria em lugar
            // nenhum. Melhor recusar e deixar a origem escolher.
            if (normalizadas.TryGetValue(normalizada, out var anterior))
            {
                erros.Add(
                    $"{prefixo}Extensions: as chaves \"{anterior}\" e \"{chave}\" viram a mesma " +
                    $"coisa no bilhete (\"{normalizada}\"). Renomeie uma delas na origem.");
            }
            else
            {
                normalizadas[normalizada] = chave;
            }
        }
    }

    private static void AddDuplicateIdErrors(IReadOnlyList<CentralizeEntity> items, List<string> erros)
    {
        var vistos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            var id = items[i]?.UniqueId;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (vistos.TryGetValue(id, out var primeiro))
            {
                erros.Add(
                    $"UniqueId \"{id}\" aparece duas vezes no mesmo lote " +
                    $"(posições {primeiro} e {i}).");
            }
            else
            {
                vistos[id] = i;
            }
        }
    }
}
