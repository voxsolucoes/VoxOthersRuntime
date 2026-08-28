using System.Text;
using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Monta o conteúdo do campo <c>CP</c> (USER_DATA) do bilhete.
/// </summary>
/// <remarks>
/// <para>
/// É onde os campos livres da origem chegam ao Vox. O formato é um XML plano,
/// sem recuo e sem declaração, exatamente como o sistema atual produz:
/// </para>
/// <code>&lt;UserInfo&gt;&lt;SERVICO&gt;CC-Clientes&lt;/SERVICO&gt;&lt;UNIQUEIDOTHERS&gt;97781&lt;/UNIQUEIDOTHERS&gt;&lt;/UserInfo&gt;</code>
/// <para>
/// Escrito à mão, e não com <c>XmlWriter</c>, de propósito: o <c>XmlWriter</c>
/// insere declaração, escolhe aspas e escapa caracteres com critério próprio.
/// Qualquer uma dessas escolhas quebraria a igualdade byte a byte com o bilhete
/// que o Vox já recebe hoje.
/// </para>
/// </remarks>
public static class UserDataBuilder
{
    /// <summary>
    /// Chave que carrega o identificador do item dentro do <c>UserInfo</c>.
    /// É por ela que o Vox reencontra a gravação depois de importada.
    /// </summary>
    public const string UniqueIdKey = "UNIQUEIDOTHERS";

    /// <summary>
    /// Monta o XML dos campos livres, acrescentando o identificador do item.
    /// </summary>
    public static string Build(CentralizeEntity entity)
    {
        var sb = new StringBuilder(256);
        sb.Append("<UserInfo>");

        if (entity.Extensions is { Count: > 0 })
        {
            foreach (var (chave, valor) in entity.Extensions)
            {
                // A chave já vem normalizada da conferência de contrato
                // (espaço vira '_'). Normalizar de novo aqui é barato e garante
                // que ninguém monte bilhete por um caminho que pulou a validação.
                var tag = CentralizeValidator.NormalizeExtensionKey(chave);
                if (tag.Length == 0) continue;

                sb.Append('<').Append(tag).Append('>');
                Escapar(sb, valor);
                sb.Append("</").Append(tag).Append('>');
            }
        }

        // Sempre por último, e sempre presente: é a âncora da rastreabilidade.
        // Se a origem já mandou uma chave com este nome, a nossa prevalece —
        // duas âncoras diferentes seria pior do que uma repetida.
        sb.Append('<').Append(UniqueIdKey).Append('>');
        Escapar(sb, entity.UniqueId);
        sb.Append("</").Append(UniqueIdKey).Append('>');

        sb.Append("</UserInfo>");
        return sb.ToString();
    }

    /// <summary>
    /// Escapa apenas o que quebraria o XML. Nada além disso: escapar a mais
    /// mudaria bytes que hoje passam intactos.
    /// </summary>
    private static void Escapar(StringBuilder sb, string? valor)
    {
        if (string.IsNullOrEmpty(valor)) return;

        foreach (var c in valor)
        {
            switch (c)
            {
                case '&': sb.Append("&amp;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                default: sb.Append(c); break;
            }
        }
    }
}
