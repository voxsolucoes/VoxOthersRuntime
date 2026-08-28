namespace VoxOthers.Runtime.Sinks;

/// <summary>
/// Leva um arquivo para a árvore de gravação do Vox sem deixar o importador ver
/// arquivo pela metade.
/// </summary>
/// <remarks>
/// <para>
/// Existe num lugar só porque agora há dois tipos de arquivo indo para a mesma
/// árvore — a gravação de voz e o anexo de chat — e a mecânica é delicada
/// demais para ser escrita duas vezes. É o mesmo raciocínio do
/// <see cref="TicketPublisher"/>: código de concorrência duplicado é código que
/// vai ser corrigido pela metade.
/// </para>
/// <para>
/// Estático de propósito. Não guarda estado nenhum, e transformá-lo em serviço
/// obrigaria a mudar o construtor de quem já o usa sem que nada melhorasse.
/// </para>
/// </remarks>
internal static class GravFileCopier
{
    /// <summary>
    /// Copia a origem para o destino final e apaga a origem.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Copia para um nome temporário <b>dentro da pasta de destino</b> e só
    /// então renomeia para o nome final. Renomear no mesmo diretório é
    /// indivisível: o arquivo aparece completo ou não aparece. Um
    /// <c>File.Move</c> direto seria indivisível apenas se origem e destino
    /// estivessem no mesmo volume — e a origem costuma ser pasta de rede, caso
    /// em que o .NET faz copiar-e-apagar e uma queda no meio deixaria um
    /// arquivo truncado já com o nome definitivo.
    /// </para>
    /// <para>
    /// Se o destino já existe, o item está sendo reprocessado: o arquivo fica
    /// como está e a origem é descartada. Sobrescrever seria pior — o que o Vox
    /// já indexou sumiria por um instante.
    /// </para>
    /// </remarks>
    /// <returns>
    /// <c>true</c> se foi esta chamada que colocou o arquivo; <c>false</c> se ele
    /// já estava lá.
    /// </returns>
    public static async Task<bool> CopyAsync(
        string origem,
        string destinoFinal,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destinoFinal))
        {
            logger.Here().Info(
                "Arquivo já estava na pasta de gravação: {Destino}. Reprocessamento.", destinoFinal);
            ApagarOrigem(origem, logger);
            return false;
        }

        var temporario = destinoFinal + ".parcial-" + Guid.CreateVersion7().ToString("n");

        try
        {
            await using (var leitura = new FileStream(
                origem, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            await using (var escrita = new FileStream(
                temporario, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await leitura.CopyToAsync(escrita, cancellationToken);
            }

            File.Move(temporario, destinoFinal, overwrite: false);
        }
        catch (Exception ex)
        {
            Descartar(temporario, logger);

            if (ex is IOException && File.Exists(destinoFinal))
            {
                // Outro worker colocou o mesmo arquivo enquanto esta cópia
                // acontecia. O resultado desejado já está lá.
                logger.Here().Info("Arquivo {Destino} foi colocado por outro worker.", destinoFinal);
                ApagarOrigem(origem, logger);
                return false;
            }

            throw;
        }

        logger.Here().Debug("Arquivo colocado: {Origem} -> {Destino}", origem, destinoFinal);
        ApagarOrigem(origem, logger);
        return true;
    }

    /// <summary>
    /// Apaga a origem depois que a cópia chegou inteira ao destino.
    /// </summary>
    /// <remarks>
    /// Falhar aqui não invalida a importação — o arquivo está no lugar certo.
    /// Só fica o aviso, porque origem não apagada acumula e um dia enche o disco
    /// de quem entrega os arquivos.
    /// </remarks>
    private static void ApagarOrigem(string origem, ILogger logger)
    {
        try
        {
            File.Delete(origem);
        }
        catch (Exception ex)
        {
            logger.Here().Warn(ex,
                "Arquivo importado, mas não foi possível apagar a origem {Origem}.", origem);
        }
    }

    private static void Descartar(string caminho, ILogger logger)
    {
        try
        {
            if (File.Exists(caminho)) File.Delete(caminho);
        }
        catch (Exception ex)
        {
            logger.Here().Warn(ex, "Não foi possível remover o arquivo parcial {Caminho}.", caminho);
        }
    }
}
