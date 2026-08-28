using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Vox.RegBDLib;
using VoxOthers.Contracts;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Traduz um atendimento de texto no bilhete <c>.CHT</c> da <c>RegBDLib</c>.
/// </summary>
/// <remarks>
/// <para>
/// Atendimento de texto <b>não</b> gera <c>.GRF</c>. O sistema atual usa um
/// bilhete e uma extensão próprios — <c>CRegBD261ChatTkt</c> gravado como
/// <c>.CHT</c> — e a conversa inteira viaja serializada em JSON no campo
/// <c>Json</c>. Referência: <c>WSNetChat.TicketGenerator</c>.
/// </para>
/// <para>
/// Separado do sink que grava em disco pelo mesmo motivo do
/// <see cref="GrfTicketFactory"/>: assim o mapeamento pode ser conferido em
/// teste sem tocar no sistema de arquivos.
/// </para>
/// </remarks>
public sealed class ChatTicketFactory
{
    /// <summary>
    /// Formato do JSON da conversa.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> é
    /// deliberado.</b> O padrão do <c>System.Text.Json</c> escapa tudo o que não
    /// é ASCII: "não" viraria <c>não</c> e um emoji viraria um par de
    /// escapes. Continua sendo JSON válido, mas o sistema atual usa Newtonsoft,
    /// que escreve o texto cru — e este bilhete precisa ser lido pelo mesmo
    /// importador. O "unsafe" do nome se refere a embutir o resultado em HTML
    /// sem tratar; aqui ele vai para dentro de um campo de bilhete, não para uma
    /// página.
    /// </para>
    /// <para>
    /// Sem política de nomes: os nomes das propriedades vão exatamente como
    /// declarados em <see cref="ChatTranscript"/>, que é o que o outro lado
    /// espera.
    /// </para>
    /// </remarks>
    private static readonly JsonSerializerOptions Formato = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public CRegBD261ChatTkt Create(ChatTicketInput input)
    {
        var e = input.Entity;

        return new CRegBD261ChatTkt
        {
            // Identificação do destino
            Server = input.ServerName,
            ServerID = e.ServerId,
            OperationID = e.OperationId,
            Channel = input.ChannelNumber,
            Ramal = input.ChannelNumber.ToString(CultureInfo.InvariantCulture),

            // Operador
            CodLogin = ParseCodLogin(input.CodLogin),
            UserID = input.UserCodeUsuario,

            // Campos livres, no mesmo formato do bilhete de voz
            UserData = UserDataBuilder.Build(e),

            // A conversa
            Json = JsonSerializer.Serialize(Transcrever(e, input.Source, input.OperatorName), Formato)

            // IncompleteChat fica falso. No sistema atual ele marca conversa do
            // dia corrente, que ainda pode receber mensagem — o conector varre a
            // origem e não sabe se o atendimento acabou. Aqui o Builder só envia
            // atendimento encerrado, então a conversa que chega está completa por
            // construção.
        };
    }

    /// <summary>
    /// Monta a conversa que vai no campo <c>Json</c>.
    /// </summary>
    internal static ChatTranscript Transcrever(CentralizeEntity e, string source, string operatorName)
    {
        var inicio = e.StartedAt;

        // Ordenação estável: mensagem fora de ordem na origem é corrigida, e
        // mensagens com o mesmo instante — comum quando a origem só informa o
        // minuto — mantêm a ordem em que chegaram. Confiar na ordem da lista
        // sozinha deixaria a conversa embaralhada; ordenar sem estabilidade
        // embaralharia justamente as que a origem já tinha ordenado.
        var mensagens = e.Messages
            .OrderBy(m => m.SentAt)
            .Select(m => new ChatTranscriptItem
            {
                StartSeconds = Deslocamento(inicio, m.SentAt),
                Text = m.Text ?? string.Empty,
                Participant = m.Author == ChatAuthor.Agent ? 0 : 1
            })
            .ToList();

        return new ChatTranscript
        {
            UniqueIDOthers = e.UniqueId,
            Source = source,
            StartChat = inicio.LocalDateTime,
            UserAgent = operatorName,
            Client = e.ContactName,
            OnlyANI = e.Ani,
            ChatDuration = e.DurationSeconds,
            ChatItems = mensagens
        };
    }

    /// <summary>
    /// Segundos entre o início do atendimento e a mensagem.
    /// </summary>
    /// <remarks>
    /// Nunca negativo. Mensagem anterior ao início do atendimento é relógio
    /// dessincronizado na origem, não viagem no tempo; um deslocamento negativo
    /// posicionaria a mensagem fora da conversa na tela do Vox. Zero é a posição
    /// mais próxima da verdade — a primeira.
    /// </remarks>
    private static long Deslocamento(DateTimeOffset inicio, DateTimeOffset mensagem)
    {
        var segundos = (long)(mensagem - inicio).TotalSeconds;
        return segundos < 0 ? 0 : segundos;
    }

    /// <summary>
    /// O bilhete de chat guarda o login como número, e não como texto.
    /// </summary>
    /// <remarks>
    /// A conversão não pode falhar: o valor vem do cadastro, que o obteve da
    /// própria base. Se falhar, é defeito de programação e não dado ruim — daí
    /// não ser <see cref="ItemRejectedException"/>, que mandaria o item para a
    /// quarentena como se a origem tivesse errado.
    /// </remarks>
    private static long ParseCodLogin(string codLogin)
    {
        if (long.TryParse(codLogin, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numero))
        {
            return numero;
        }

        throw new InvalidOperationException(
            $"Código de login '{codLogin}' não é um número. O bilhete de chat exige número.");
    }
}

/// <summary>
/// O que o bilhete de chat precisa e que não vem do contrato.
/// </summary>
public sealed record ChatTicketInput
{
    public required CentralizeEntity Entity { get; init; }

    /// <summary>Nome do servidor Vox.</summary>
    public required string ServerName { get; init; }

    /// <summary>Canal alocado para o atendimento.</summary>
    public required int ChannelNumber { get; init; }

    /// <summary>Nome do operador, como aparece na conversa.</summary>
    public required string OperatorName { get; init; }

    /// <summary>Código do login resolvido na base.</summary>
    public required string CodLogin { get; init; }

    /// <summary>Código do usuário resolvido na base.</summary>
    public required string UserCodeUsuario { get; init; }

    /// <summary>Origem do lote.</summary>
    public required string Source { get; init; }
}
