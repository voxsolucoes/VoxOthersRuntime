using Vox.RegBDLib;

namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Como o bilhete é serializado em disco.
/// </summary>
/// <remarks>
/// Não é escolha nossa: cada tipo de bilhete da <c>RegBDLib</c> tem o seu, e o
/// importador do Vox espera exatamente aquele. <c>.GRF</c> e <c>.CHT</c> são
/// texto; o <c>.CHW</c> é XML.
/// </remarks>
public enum FormatoDoBilhete
{
    Texto,
    Xml
}

/// <summary>
/// Publica um bilhete na pasta onde o importador do Vox o procura.
/// </summary>
/// <remarks>
/// <para>
/// Vale para qualquer bilhete da <c>RegBDLib</c> — o <c>.GRF</c> da gravação de
/// voz, o <c>.CHT</c> do atendimento de texto e o <c>.CHW</c> do texto com
/// anexo herdam do mesmo <see cref="CBaseRegBDTkt"/>. O que muda entre eles é a
/// extensão e a forma de serializar; a mecânica de escrever sem deixar o
/// importador ver arquivo pela metade é idêntica, e por isso mora num lugar só.
/// </para>
/// <para>
/// <b>Duas etapas, e não uma.</b> O <c>SaveTicket</c> da <c>RegBDLib</c> escolhe
/// o nome procurando o primeiro sufixo livre (<c>nome0</c>, <c>nome1</c>, …) com
/// <c>File.Exists</c> num laço, e só depois grava. Entre a checagem e a gravação
/// existe uma janela, e ela foi medida: 50 gravações simultâneas na mesma pasta
/// produzem por volta de 17 bilhetes. Cerca de 19 falham com
/// <c>IOException</c> — ruim, mas visível — e outros 14 são sobrescritos sem erro
/// nenhum. Esses últimos são o problema: o worker dá o item por entregue e a
/// gravação não existe.
/// </para>
/// <para>
/// A solução não mexe na biblioteca. Cada bilhete é escrito numa pasta de
/// trabalho exclusiva, onde não há com quem competir, e depois movido para o
/// destino com <see cref="File.Move(string, string, bool)"/> e
/// <c>overwrite: false</c>. Aí o sistema de arquivos decide: se o nome já existe,
/// o move falha e tentamos o próximo sufixo. A checagem e a reserva do nome viram
/// uma coisa só, indivisível.
/// </para>
/// </remarks>
public sealed class TicketPublisher
{
    private const int LimiteDeTentativas = 1000;

    private readonly ILogger<TicketPublisher> _logger;

    public TicketPublisher(ILogger<TicketPublisher> logger) => _logger = logger;

    /// <summary>
    /// Grava o bilhete e devolve o caminho final.
    /// </summary>
    /// <param name="extensao">
    /// Sem ponto: <c>GRF</c> para voz, <c>CHT</c> para texto, <c>CHW</c> para
    /// texto com anexo.
    /// </param>
    /// <param name="formato">
    /// Como serializar. Errar aqui gera um arquivo com a extensão certa e o
    /// conteúdo que o importador não sabe ler.
    /// </param>
    public string Publish(
        CBaseRegBDTkt tkt,
        string extensao,
        string pastaDeTrabalho,
        string pastaDeRegistro,
        string nomeBase,
        FormatoDoBilhete formato,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pastaExclusiva = Path.Combine(pastaDeTrabalho, Guid.CreateVersion7().ToString("n"));
        Directory.CreateDirectory(pastaExclusiva);

        try
        {
            if (formato == FormatoDoBilhete.Xml)
            {
                tkt.SaveXmlTicket(pastaExclusiva, nomeBase, extensao, false);
            }
            else
            {
                tkt.SaveTicket(pastaExclusiva, nomeBase, extensao);
            }

            var gerado = Directory.GetFiles(pastaExclusiva).Single();

            Directory.CreateDirectory(pastaDeRegistro);
            return MoverParaRegistro(gerado, pastaDeRegistro, nomeBase, extensao);
        }
        finally
        {
            try
            {
                Directory.Delete(pastaExclusiva, recursive: true);
            }
            catch (IOException ex)
            {
                // Não é motivo para perder o bilhete que já foi entregue.
                _logger.Here().Warn(ex, "Não foi possível limpar a pasta de trabalho {Pasta}", pastaExclusiva);
            }
        }
    }

    private static string MoverParaRegistro(
        string origem,
        string pastaDeRegistro,
        string nomeBase,
        string extensao)
    {
        for (var sufixo = 0; sufixo < LimiteDeTentativas; sufixo++)
        {
            var destino = Path.Combine(pastaDeRegistro, $"{nomeBase}{sufixo}.{extensao}");

            try
            {
                File.Move(origem, destino, overwrite: false);
                return destino;
            }
            catch (IOException) when (File.Exists(destino))
            {
                // Nome tomado — por outro worker ou por uma execução anterior.
                // Segue para o próximo sufixo, que é o mesmo comportamento
                // visível do sistema atual.
            }
        }

        throw new IOException(
            $"Não foi possível gravar o bilhete: os {LimiteDeTentativas} primeiros nomes " +
            $"baseados em '{nomeBase}' já existem em {pastaDeRegistro}.");
    }
}
