using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>
/// Confere se o item tem conteúdo para importar antes de mexer na base do Vox.
/// </summary>
/// <remarks>
/// <para>
/// Roda <b>antes</b> do cadastro do operador de propósito. Item sem conteúdo não
/// vai entrar no Vox de jeito nenhum; conferir depois criaria usuário, ramal e
/// login para um atendimento que termina na quarentena — lixo permanente numa
/// base que os dois sistemas compartilham durante a migração.
/// </para>
/// <para>
/// A mídia de voz não é conferida aqui, e sim no momento de colocá-la na árvore
/// de gravação. Não é inconsistência: lá a conferência e a cópia precisam estar
/// coladas, senão o arquivo pode sumir ou mudar entre uma e outra. O anexo é
/// conferido nos dois lugares — aqui para o item nem chegar a mexer na base, e
/// lá de novo porque entre uma coisa e outra o arquivo pode ter sumido.
/// </para>
/// </remarks>
public static class ContentValidation
{
    public static void Conferir(CentralizeEntity entity)
    {
        if (entity.Kind == MediaKind.Chat)
        {
            ConferirChat(entity);
        }

        ConferirAnexos(entity);
    }

    /// <summary>
    /// Atendimento de texto precisa ter conteúdo.
    /// </summary>
    /// <remarks>
    /// Chat sem mensagem e sem arquivo geraria um bilhete apontando para
    /// atendimento vazio: aparece na busca do Vox, e quem abrir não encontra
    /// nada. Recusar é melhor que importar um registro que só faz perder tempo
    /// de quem procura.
    /// </remarks>
    private static void ConferirChat(CentralizeEntity entity)
    {
        var temMensagem = entity.Messages.Any(m => !string.IsNullOrWhiteSpace(m.Text));
        var temArquivo = !string.IsNullOrWhiteSpace(entity.MediaPath);
        var temAnexo = entity.Attachments.Count > 0
                       || entity.Messages.Any(m => m.Attachment is not null);

        if (!temMensagem && !temArquivo && !temAnexo)
        {
            throw new ItemRejectedException(
                "Atendimento de texto sem mensagem, sem arquivo e sem anexo: não há conteúdo para importar.");
        }
    }

    /// <summary>
    /// Anexo prometido tem de existir.
    /// </summary>
    /// <remarks>
    /// O contrato diz que o Builder deixa o arquivo onde o Runtime enxergue.
    /// Quando isso não acontece, a falha apareceria só na hora de gerar o
    /// bilhete — depois de o operador já ter sido cadastrado. Conferir agora
    /// transforma isso num item em quarentena, com o caminho exato que faltou.
    /// </remarks>
    private static void ConferirAnexos(CentralizeEntity entity)
    {
        for (var i = 0; i < entity.Attachments.Count; i++)
        {
            Conferir(entity.Attachments[i], $"Anexo {i + 1} de {entity.Attachments.Count}");
        }

        for (var i = 0; i < entity.Messages.Count; i++)
        {
            var anexo = entity.Messages[i].Attachment;

            if (anexo is not null)
            {
                Conferir(anexo, $"Anexo da mensagem {i + 1}");
            }
        }
    }

    /// <summary>
    /// A descrição diz <b>qual</b> anexo faltou.
    /// </summary>
    /// <remarks>
    /// Uma conversa pode ter vários arquivos. "Anexo não encontrado" sem dizer
    /// qual obriga quem for corrigir a conferir todos, um a um.
    /// </remarks>
    private static void Conferir(MediaAttachment anexo, string descricao)
    {
        if (string.IsNullOrWhiteSpace(anexo.Path))
        {
            throw new ItemRejectedException($"{descricao} veio sem caminho de arquivo.");
        }

        if (!File.Exists(anexo.Path))
        {
            throw new ItemRejectedException($"{descricao} não encontrado: '{anexo.Path}'.");
        }
    }
}
