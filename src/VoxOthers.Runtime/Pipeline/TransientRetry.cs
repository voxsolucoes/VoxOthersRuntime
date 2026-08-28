using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;

namespace VoxOthers.Runtime.Pipeline;

/// <summary>
/// Repete a operação quando a falha é passageira, com espera crescente.
/// </summary>
/// <remarks>
/// <para>
/// <b>O que é passageiro e o que não é</b> já estava decidido desde a Fase 3, e
/// esta classe só passou a agir sobre a decisão:
/// <see cref="ItemRejectedException"/> é problema do dado e repetir não muda
/// nada — arquivo que não existe continua não existindo na terceira tentativa.
/// Qualquer outra exceção é do ambiente: rede, banco, disco, pasta ocupada. Essa
/// vale repetir.
/// </para>
/// <para>
/// <b>Nem toda falha transitória chega como exceção.</b> A validação captura os
/// erros de infraestrutura por dentro e devolve
/// <see cref="QuarantineKind.Infraestrutura"/> num resultado, sem estourar.
/// Por isso existe o predicado <c>precisaRepetir</c>: sem ele, o banco fora do
/// ar mandaria o item direto para a quarentena, que é justamente o que esta
/// fase promete eliminar.
/// </para>
/// <para>
/// <b>Espera crescente</b> (5s, 10s, 20s…) e não fixa. Falha de infraestrutura
/// raramente dura menos que a primeira espera, e insistir em intervalo curto
/// só aumenta a carga em cima de um recurso que já está mal.
/// </para>
/// </remarks>
public sealed class TransientRetry
{
    /// <summary>
    /// Teto da espera entre tentativas.
    /// </summary>
    /// <remarks>
    /// Sem teto, uma espera base de 300 segundos com 10 tentativas chegaria a
    /// mais de um dia na última — o item ficaria preso ocupando um worker.
    /// </remarks>
    private static readonly TimeSpan EsperaMaxima = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<RuntimeOptions> _options;
    private readonly TimeProvider _tempo;
    private readonly ILogger<TransientRetry> _logger;

    public TransientRetry(
        IOptionsMonitor<RuntimeOptions> options,
        TimeProvider tempo,
        ILogger<TransientRetry> logger)
    {
        _options = options;
        _tempo = tempo;
        _logger = logger;
    }

    /// <summary>
    /// Executa a operação, repetindo enquanto a falha for passageira.
    /// </summary>
    /// <param name="precisaRepetir">
    /// Diz se o <b>resultado</b> devolvido é uma falha transitória. Para
    /// operações que só falham por exceção, passe <c>_ =&gt; false</c>.
    /// </param>
    /// <returns>
    /// O resultado da última tentativa. Se as tentativas acabarem e a operação
    /// ainda estiver falhando por exceção, a exceção sobe.
    /// </returns>
    public async Task<T> ExecutarAsync<T>(
        Func<CancellationToken, Task<T>> operacao,
        Func<T, bool> precisaRepetir,
        string descricao,
        CancellationToken cancellationToken)
    {
        // Zero e um significam a mesma coisa: tenta uma vez e pronto.
        var limite = Math.Max(1, _options.CurrentValue.Retry.MaxAttempts);

        for (var tentativa = 1; ; tentativa++)
        {
            var ultima = tentativa >= limite;

            try
            {
                var resultado = await operacao(cancellationToken);
                var falhou = precisaRepetir(resultado);

                if (!falhou || ultima)
                {
                    if (!falhou && tentativa > 1)
                    {
                        _logger.Here().Info(
                            "{Descricao} funcionou na tentativa {Tentativa}.", descricao, tentativa);
                    }

                    return resultado;
                }

                _logger.Here().Warn(
                    "{Descricao} falhou por problema de ambiente (tentativa {Tentativa} de {Limite}).",
                    descricao, tentativa, limite);
            }
            catch (OperationCanceledException)
            {
                // Desligamento ou prazo estourado. Repetir aqui atrasaria o
                // encerramento do serviço sem nenhum ganho.
                throw;
            }
            catch (ItemRejectedException)
            {
                // Problema do dado: a terceira tentativa daria o mesmo erro.
                throw;
            }
            catch (Exception ex) when (!ultima)
            {
                _logger.Here().Warn(ex,
                    "{Descricao} falhou (tentativa {Tentativa} de {Limite}): {Motivo}",
                    descricao, tentativa, limite, ex.Message);
            }

            await Task.Delay(Espera(tentativa), _tempo, cancellationToken);
        }
    }

    /// <summary>
    /// Espera antes da próxima tentativa: dobra a cada vez, até o teto.
    /// </summary>
    internal TimeSpan Espera(int tentativa)
    {
        var baseSegundos = _options.CurrentValue.Retry.BaseDelaySeconds;

        // O expoente é limitado antes da multiplicação: sem isso, uma
        // configuração alta estoura o double e a espera vira infinito.
        var fator = Math.Pow(2, Math.Min(tentativa - 1, 16));
        var segundos = baseSegundos * fator;

        return segundos >= EsperaMaxima.TotalSeconds
            ? EsperaMaxima
            : TimeSpan.FromSeconds(segundos);
    }
}
