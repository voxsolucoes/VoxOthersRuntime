using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Sinks;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>
/// Livro do que já foi importado. É o que impede o mesmo item de entrar duas
/// vezes.
/// </summary>
/// <remarks>
/// Substitui o controle por arquivo XML por conector do sistema atual, que era
/// reciclado à virada do dia — o que fazia um item reprocessado no dia seguinte
/// ser importado de novo, e impedia duas instâncias do serviço de coexistir
/// (AD-5).
/// </remarks>
public interface IImportLedger
{
    /// <summary>Diz se o item já foi importado nesta operação.</summary>
    Task<bool> JaImportadoAsync(int operationId, string uniqueId, CancellationToken cancellationToken);

    /// <summary>
    /// Registra o item como importado.
    /// </summary>
    /// <returns>
    /// <c>false</c> se outra instância registrou o mesmo item primeiro; nesse
    /// caso o registro daqui é descartado sem erro.
    /// </returns>
    Task<bool> RegistrarAsync(
        ImportedItemContext context,
        string ticketReference,
        CancellationToken cancellationToken);
}

/// <summary>
/// Deduplicação por marcador em disco: um arquivo por item importado.
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que arquivo e não tabela.</b> A base do Vox é compartilhada com o
/// sistema atual durante a migração, e criar tabela nela é mexer no que não é
/// nosso. Um banco só do Runtime resolveria isso, mas acrescentaria um servidor
/// a instalar, monitorar e fazer backup para guardar uma linha por atendimento.
/// A pasta compartilhada já existe — é a mesma natureza da pasta de registro e
/// da de quarentena.
/// </para>
/// <para>
/// <b>O que substitui o índice único.</b> O marcador é criado com
/// <see cref="File.Move(string, string, bool)"/> e <c>overwrite: false</c>: quem
/// chega primeiro cria, quem chega depois recebe erro. A decisão de quem ganhou
/// é do sistema de arquivos, numa operação indivisível — exatamente o que o
/// índice único fazia, e pelo mesmo motivo. Não há conferir-e-depois-gravar, que
/// é onde mora a corrida.
/// </para>
/// </remarks>
public sealed class ImportLedger : IImportLedger
{
    private const string Extensao = ".ok";

    private readonly IOptionsMonitor<DeduplicationOptions> _options;
    private readonly ILogger<ImportLedger> _logger;

    public ImportLedger(IOptionsMonitor<DeduplicationOptions> options, ILogger<ImportLedger> logger)
    {
        _options = options;
        _logger = logger;
    }

    public Task<bool> JaImportadoAsync(int operationId, string uniqueId, CancellationToken cancellationToken)
    {
        var caminho = Path.Combine(Raiz(), CaminhoDoMarcador(operationId, uniqueId));

        return Task.FromResult(File.Exists(caminho));
    }

    public async Task<bool> RegistrarAsync(
        ImportedItemContext context,
        string ticketReference,
        CancellationToken cancellationToken)
    {
        var destino = Path.Combine(Raiz(), CaminhoDoMarcador(context.OperationId, context.UniqueId));
        Directory.CreateDirectory(Path.GetDirectoryName(destino)!);

        // O temporário tem nome único por tentativa: duas instâncias registrando
        // o mesmo item ao mesmo tempo não podem disputar o arquivo intermediário,
        // senão a corrida só mudaria de lugar.
        var temporario = $"{destino}.{Guid.CreateVersion7():n}.parcial";

        try
        {
            await File.WriteAllTextAsync(temporario, Conteudo(context, ticketReference), cancellationToken);

            File.Move(temporario, destino, overwrite: false);

            return true;
        }
        catch (IOException) when (File.Exists(destino))
        {
            // A consulta de duplicidade acontece antes da entrega; entre uma
            // coisa e outra, outra instância pode ter registrado o mesmo item.
            // Não é erro: significa que o item está importado, que é o
            // resultado que se queria. Só o bilhete duplicado precisa de
            // atenção, e por isso o aviso.
            _logger.Here().Warn(
                "Item {UniqueId} da operação {OperationId} já havia sido registrado por outra instância. " +
                "O bilhete {Ticket} gerado aqui é duplicado.",
                context.UniqueId, context.OperationId, ticketReference);

            Descartar(temporario);
            return false;
        }
        catch
        {
            Descartar(temporario);
            throw;
        }
    }

    /// <summary>
    /// Devolve a pasta raiz, garantindo que ela existe.
    /// </summary>
    /// <remarks>
    /// Criar a pasta a cada chamada parece desperdício e é proposital.
    /// <see cref="File.Exists(string)"/> devolve <c>false</c> quando o caminho
    /// está inacessível — com a pasta de rede fora do ar, <b>todo</b> item
    /// pareceria novo e seria importado de novo, aos milhares, sem um único erro
    /// no log. <see cref="Directory.CreateDirectory(string)"/> estoura nessa
    /// situação, e o item vira falha de ambiente: espera e reprocessa. É a
    /// diferença entre uma falha visível e uma enxurrada silenciosa de bilhetes
    /// duplicados. O custo é uma chamada ao sistema de arquivos por item, ao lado
    /// da gravação de um bilhete inteiro.
    /// </remarks>
    private string Raiz()
    {
        var raiz = _options.CurrentValue.Path;
        Directory.CreateDirectory(raiz);

        return raiz;
    }

    /// <summary>
    /// Caminho relativo do marcador: <c>op-{operação}\{2 dígitos}\{hash}.ok</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Visível para a consulta de rastreio, que precisa achar o marcador de um
    /// item. Recalcular o hash lá seria repetir esta regra em dois lugares — e
    /// no dia em que um deles mudasse, a consulta passaria a responder "não
    /// importado" para item importado, que é a pior resposta possível.
    /// </para>
    /// <para>
    /// <b>Por que o nome é um hash e não o identificador.</b> O identificador vem
    /// de sistema de terceiro e pode ter barra, dois-pontos ou qualquer coisa que
    /// não serve em nome de arquivo. Trocar os caracteres proibidos por
    /// <c>_</c> — como a quarentena faz — resolveria o nome e criaria um defeito
    /// pior: <c>a/b</c> e <c>a\b</c> virariam o mesmo arquivo, e o segundo
    /// atendimento seria descartado como duplicata sem nunca ter sido importado.
    /// Perda silenciosa. O hash é sempre um nome válido, tem tamanho fixo e não
    /// junta identificadores diferentes.
    /// </para>
    /// <para>
    /// <b>Por que as subpastas.</b> A consulta é um <c>File.Exists</c> direto, mas
    /// o expurgo por idade precisa listar. Separando por operação e pelos dois
    /// primeiros dígitos do hash, nenhuma pasta cresce sem limite e a varredura
    /// de retenção trabalha um pedaço de cada vez.
    /// </para>
    /// </remarks>
    internal static string CaminhoDoMarcador(int operationId, string uniqueId)
    {
        var digest = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(uniqueId))).ToLowerInvariant();

        return Path.Combine($"op-{operationId}", digest[..2], digest + Extensao);
    }

    /// <summary>
    /// Conteúdo do marcador. O arquivo poderia ser vazio — existir já é a
    /// resposta —, mas então ninguém saberia a que atendimento ele se refere,
    /// já que o nome é um hash.
    /// </summary>
    private static string Conteudo(ImportedItemContext context, string ticketReference) =>
        string.Join(Environment.NewLine,
        [
            $"uniqueId={context.UniqueId}",
            $"operacao={context.OperationId}",
            $"importadoEm={DateTimeOffset.Now:O}",
            $"bilhete={ticketReference}",
            $"canal={context.ChannelNumber}",
            $"usuario={context.UserName}"
        ]);

    /// <summary>
    /// Some com o temporário que não virou marcador. Falhar aqui não muda nada
    /// para o item — o que sobra é lixo que o expurgo recolhe depois.
    /// </summary>
    private void Descartar(string temporario)
    {
        try
        {
            File.Delete(temporario);
        }
        catch (Exception ex)
        {
            _logger.Here().Debug(ex, "Não foi possível apagar o arquivo temporário {Caminho}", temporario);
        }
    }
}
