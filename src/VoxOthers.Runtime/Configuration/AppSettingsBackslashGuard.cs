using System.Text;
using System.Text.Json;

namespace VoxOthers.Runtime.Configuration;

/// <summary>
/// Reparo dos caminhos com barra simples nos appsettings.
/// </summary>
/// <remarks>
/// <para>
/// Em JSON, uma barra simples em <c>"C:\Simulacao\Grav"</c> é um escape
/// inválido — e o parse falha com o ARQUIVO INTEIRO, não só com o caminho.
/// Quem digita à mão (suporte, instalação) esquece facilmente a segunda barra.
/// </para>
/// <para>
/// Como o <c>WebApplication.CreateBuilder</c> lê o arquivo dentro do seu
/// próprio construtor, não dá para trocar as fontes de configuração depois:
/// o erro já teria acontecido. O guard roda ANTES do <c>CreateBuilder</c>, lê
/// o texto bruto e, quando o JSON só está quebrado por barra solta, reescreve
/// o arquivo já com a barra dupla — preservando comentários, vírgula final,
/// encoding e BOM. Arquivo que já é JSON válido não é tocado.
/// </para>
/// </remarks>
public static class AppSettingsBackslashGuard
{
    private static readonly JsonDocumentOptions OpcoesJson = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Aplica o reparo em todos os <c>appsettings*.json</c> do diretório de
    /// conteúdo (o mesmo de onde o host lê a configuração). Retorna quantos
    /// arquivos precisaram ser reescritos.
    /// </summary>
    /// <param name="diretorio">
    /// Diretório a varrer; quando omitido, o mesmo do host (variável de
    /// ambiente de content root ou diretório de trabalho).
    /// </param>
    public static int RepararTodosOsAppSettings(string? diretorio = null)
    {
        diretorio ??= DiretorioDeConteudo;

        if (!Directory.Exists(diretorio))
        {
            return 0;
        }

        var reparados = 0;
        foreach (var caminho in Directory.EnumerateFiles(diretorio, "appsettings*.json", SearchOption.TopDirectoryOnly))
        {
            if (RepararSeNecessario(caminho))
            {
                reparados++;
            }
        }

        return reparados;
    }

    /// <summary>
    /// Duplica toda barra que não esteja já dentro de um par <c>\\</c>.
    /// </summary>
    /// <remarks>
    /// Exemplos de transformação:
    /// <c>C:\Temp\novo</c> → <c>C:\\Temp\\novo</c>;
    /// <c>C:\\Temp\\novo</c> (já correto) → inalterado. O par <c>\\</c> é
    /// reconhecido e preservado; barra solta é sempre separador de caminho.
    /// </remarks>
    internal static string DuplicarBarrasSoltas(string texto)
    {
        var saida = new StringBuilder(texto.Length + 32);

        for (var i = 0; i < texto.Length; i++)
        {
            if (texto[i] == '\\')
            {
                saida.Append('\\');

                if (i + 1 < texto.Length && texto[i + 1] == '\\')
                {
                    saida.Append('\\');
                    i++; // consome o par completo
                }
                else
                {
                    saida.Append('\\'); // barra solta → duplica
                }
            }
            else
            {
                saida.Append(texto[i]);
            }
        }

        return saida.ToString();
    }

    private static string DiretorioDeConteudo
        => Environment.GetEnvironmentVariable("ASPNETCORE_CONTENTROOT")
        ?? Environment.GetEnvironmentVariable("DOTNET_CONTENTROOT")
        ?? Directory.GetCurrentDirectory();

    private static bool RepararSeNecessario(string caminho)
    {
        byte[] conteudo;
        try
        {
            conteudo = File.ReadAllBytes(caminho);
        }
        catch
        {
            return false; // inacessível agora — o erro real aparece no CreateBuilder
        }

        if (EhJsonValido(conteudo))
        {
            return false; // já está correto (barra dupla ou sem barra)
        }

        var reparado = Reparar(conteudo);
        if (!EhJsonValido(reparado))
        {
            return false; // o problema não é barra solta — deixa o host falar
        }

        return GravarAtomicamente(caminho, reparado);
    }

    private static bool EhJsonValido(byte[] conteudo)
    {
        try
        {
            using var documento = JsonDocument.Parse(Decodificar(conteudo).Texto, OpcoesJson);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] Reparar(byte[] conteudo)
    {
        var (texto, encoding) = Decodificar(conteudo);
        var bytes = encoding.GetBytes(DuplicarBarrasSoltas(texto));

        var bom = encoding.GetPreamble();
        if (bom.Length == 0)
        {
            return bytes;
        }

        var saida = new byte[bom.Length + bytes.Length];
        Buffer.BlockCopy(bom, 0, saida, 0, bom.Length);
        Buffer.BlockCopy(bytes, 0, saida, bom.Length, bytes.Length);

        return saida;
    }

    private static (string Texto, Encoding Encoding) Decodificar(byte[] conteudo)
    {
        if (conteudo.Length >= 3 && conteudo[0] == 0xEF && conteudo[1] == 0xBB && conteudo[2] == 0xBF)
        {
            return (Encoding.UTF8.GetString(conteudo, 3, conteudo.Length - 3),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        if (conteudo.Length >= 2 && conteudo[0] == 0xFF && conteudo[1] == 0xFE)
        {
            return (Encoding.Unicode.GetString(conteudo, 2, conteudo.Length - 2), Encoding.Unicode);
        }

        if (conteudo.Length >= 2 && conteudo[0] == 0xFE && conteudo[1] == 0xFF)
        {
            return (Encoding.BigEndianUnicode.GetString(conteudo, 2, conteudo.Length - 2), Encoding.BigEndianUnicode);
        }

        // Sem BOM: mantém os bytes como estão na releitura (Latin1 é 1:1). Só a
        // ESTRUTURA do JSON importa aqui; acentos passam intactos.
        return (Encoding.Latin1.GetString(conteudo), Encoding.Latin1);
    }

    private static bool GravarAtomicamente(string caminho, byte[] conteudo)
    {
        var temporario = caminho + ".reparando-tmp";
        try
        {
            File.WriteAllBytes(temporario, conteudo);
            File.Move(temporario, caminho, overwrite: true);
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(temporario))
                {
                    File.Delete(temporario);
                }
            }
            catch
            {
                // melhor esforço: o arquivo temporário sobra, o boot segue com o erro do host
            }

            return false;
        }
    }
}
