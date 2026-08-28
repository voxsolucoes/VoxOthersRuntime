using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text;

namespace VoxOthers.Runtime.Observability;

/// <summary>
/// Guarda o valor corrente de cada indicador para que alguém consiga lê-lo.
/// </summary>
/// <remarks>
/// <para>
/// Um <see cref="Counter{T}"/> não guarda o total: ele só avisa quem estiver
/// escutando que somou tanto. Sem alguém do outro lado, medir não produz número
/// nenhum. Este é o outro lado — a mesma função que o SDK do OpenTelemetry
/// cumpriria, em tamanho suficiente para o que esta instalação precisa.
/// </para>
/// <para>
/// <b>Escuta este medidor, não o nome dele.</b> A comparação é por referência
/// ao objeto <see cref="Meter"/>. Vários serviços podem subir no mesmo processo
/// — é o que acontece na bateria de testes —, e filtrar por nome faria um somar
/// o movimento do outro, com resultado dependendo da ordem em que rodassem.
/// </para>
/// <para>
/// <b>Memória.</b> Uma entrada por combinação de rótulos, e os rótulos são de
/// conjunto fechado (ver <see cref="RuntimeMetrics"/>). Com sessenta origens, o
/// teto é da ordem de algumas centenas de entradas de poucas dezenas de bytes:
/// desprezível, e o que garante isso é a disciplina de não usar texto livre como
/// rótulo.
/// </para>
/// </remarks>
public sealed class MetricsRegistry : IDisposable
{
    private readonly ConcurrentDictionary<Chave, Acumulado> _series = new();
    private readonly MeterListener _ouvinte;
    private readonly Meter _medidor;

    public MetricsRegistry(RuntimeMetrics metricas)
    {
        ArgumentNullException.ThrowIfNull(metricas);

        _medidor = metricas.Medidor;

        _ouvinte = new MeterListener
        {
            InstrumentPublished = (instrumento, ouvinte) =>
            {
                if (ReferenceEquals(instrumento.Meter, _medidor))
                {
                    ouvinte.EnableMeasurementEvents(instrumento);
                }
            }
        };

        _ouvinte.SetMeasurementEventCallback<long>(
            (instrumento, valor, rotulos, _) => Registrar(instrumento, valor, rotulos));

        _ouvinte.SetMeasurementEventCallback<int>(
            (instrumento, valor, rotulos, _) => Registrar(instrumento, valor, rotulos));

        _ouvinte.SetMeasurementEventCallback<double>(
            (instrumento, valor, rotulos, _) => Registrar(instrumento, valor, rotulos));

        _ouvinte.Start();
    }

    /// <summary>
    /// Retrato de agora, em ordem estável.
    /// </summary>
    /// <remarks>
    /// A ordenação não é estética: sem ela, duas leituras seguidas devolveriam
    /// as mesmas linhas embaralhadas, e comparar dois retratos — que é o que se
    /// faz ao investigar — viraria trabalho manual.
    /// </remarks>
    public IReadOnlyList<Serie> Ler()
    {
        // Medidor de fila é sob demanda: só é calculado quando alguém pergunta.
        _ouvinte.RecordObservableInstruments();

        return
        [
            .. _series
                .Select(par => new Serie
                {
                    Nome = par.Key.Instrumento,
                    Unidade = par.Value.Unidade,
                    Descricao = par.Value.Descricao,
                    Rotulos = par.Value.Rotulos,
                    Valor = par.Value.Valor,
                    Ocorrencias = par.Value.Ocorrencias,
                    Maior = par.Value.Maior,
                    Acumulativo = par.Value.Acumulativo
                })
                .OrderBy(s => s.Nome, StringComparer.Ordinal)
                .ThenBy(s => s.Etiqueta, StringComparer.Ordinal)
        ];
    }

    /// <summary>
    /// Os mesmos números no formato de texto que qualquer coletor entende.
    /// </summary>
    /// <remarks>
    /// É a rota de fuga do argumento "hoje não existe coletor aqui": no dia em
    /// que existir, ele aponta para este endereço e funciona, sem esperar pela
    /// adoção do SDK.
    /// </remarks>
    public string EmTextoDeColetor()
    {
        var texto = new StringBuilder();

        foreach (var grupo in Ler().GroupBy(s => s.Nome, StringComparer.Ordinal))
        {
            var nome = SemPonto(grupo.Key);
            var primeira = grupo.First();

            texto.Append("# HELP ").Append(nome).Append(' ').AppendLine(primeira.Descricao);
            texto.Append("# TYPE ").Append(nome).AppendLine(primeira.Acumulativo ? " counter" : " gauge");

            foreach (var serie in grupo)
            {
                texto.Append(nome);

                if (serie.Rotulos.Count > 0)
                {
                    texto.Append('{')
                        .Append(string.Join(",", serie.Rotulos.Select(r =>
                            $"{SemPonto(r.Key)}=\"{Escapar(r.Value)}\"")))
                        .Append('}');
                }

                texto.Append(' ')
                    .AppendLine(serie.Valor.ToString("0.####", CultureInfo.InvariantCulture));
            }
        }

        return texto.ToString();
    }

    public void Dispose() => _ouvinte.Dispose();

    private void Registrar(Instrument instrumento, double valor, ReadOnlySpan<KeyValuePair<string, object?>> rotulos)
    {
        var lidos = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var rotulo in rotulos)
        {
            lidos[rotulo.Key] = rotulo.Value?.ToString() ?? string.Empty;
        }

        var chave = new Chave(
            instrumento.Name,
            string.Join(',', lidos.Select(r => $"{r.Key}={r.Value}")));

        // Contador soma; medidor de estado e histograma guardam o último. O
        // histograma guarda também quantas vezes e o pior caso: o total sozinho
        // não diz nada sobre tempo, e a média esconde exatamente o item lento
        // que se está procurando.
        var acumulativo = instrumento is Counter<long> or Counter<int> or Counter<double>;

        _series.AddOrUpdate(
            chave,
            _ => new Acumulado
            {
                Unidade = instrumento.Unit ?? string.Empty,
                Descricao = instrumento.Description ?? string.Empty,
                Rotulos = lidos,
                Valor = valor,
                Ocorrencias = 1,
                Maior = valor,
                Acumulativo = acumulativo
            },
            (_, atual) => atual with
            {
                Valor = acumulativo ? atual.Valor + valor : valor,
                Ocorrencias = atual.Ocorrencias + 1,
                Maior = Math.Max(atual.Maior, valor)
            });
    }

    /// <summary>Ponto não vale em nome de série para coletor; sublinhado vale.</summary>
    private static string SemPonto(string nome) => nome.Replace('.', '_');

    private static string Escapar(string valor)
        => valor.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ");

    private readonly record struct Chave(string Instrumento, string Rotulos);

    private sealed record Acumulado
    {
        public required string Unidade { get; init; }
        public required string Descricao { get; init; }
        public required SortedDictionary<string, string> Rotulos { get; init; }
        public required double Valor { get; init; }
        public required long Ocorrencias { get; init; }
        public required double Maior { get; init; }
        public required bool Acumulativo { get; init; }
    }
}

/// <summary>Um indicador com uma combinação de rótulos.</summary>
public sealed record Serie
{
    public required string Nome { get; init; }
    public required string Unidade { get; init; }
    public required string Descricao { get; init; }
    public required IReadOnlyDictionary<string, string> Rotulos { get; init; }
    public required double Valor { get; init; }

    /// <summary>Quantas medições entraram. Só interessa em histograma.</summary>
    public required long Ocorrencias { get; init; }

    /// <summary>Maior medição vista. Só interessa em histograma.</summary>
    public required double Maior { get; init; }

    /// <summary>Verdadeiro para contador; falso para medida de estado.</summary>
    public required bool Acumulativo { get; init; }

    /// <summary>Os rótulos em uma linha, para exibir e ordenar.</summary>
    public string Etiqueta => string.Join(", ", Rotulos.Select(r => $"{r.Key}={r.Value}"));
}
