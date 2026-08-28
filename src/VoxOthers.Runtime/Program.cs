using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using VoxOthers.Runtime.Configuration;
using VoxOthers.Runtime.Ingestion;
using VoxOthers.Runtime.Observability;
using VoxOthers.Runtime.Pipeline;

// -----------------------------------------------------------------------------
// Reparo de caminhos com uma barra no appsettings
// -----------------------------------------------------------------------------
// O JSON exige "\\" para um caminho Windows; uma barra só ("C:\Simulacao") é
// escape inválido e derruba o carregamento da configuração inteira. Como o
// host lê o arquivo DENTRO do CreateBuilder, o reparo precisa vir antes:
// quando necessário, corrige a barra solta para "\\" no próprio arquivo.
var configReparados = AppSettingsBackslashGuard.RepararTodosOsAppSettings();
if (configReparados > 0)
{
    Console.WriteLine(
        "AppSettingsBackslashGuard: " + configReparados +
        " arquivo(s) de configuracao tinham barra simples nos caminhos e foram reescritos com barra dupla.");
}

var builder = WebApplication.CreateBuilder(args);

// -----------------------------------------------------------------------------
// Windows Service
// -----------------------------------------------------------------------------
// Sobe sob o Service Control Manager — inicia junto com o Windows e para o
// serviço de forma controlada (drenando a fila). Fora do SCM esta linha não
// muda nada: o processo continua subindo como console, que é como os testes
// usam.
builder.Host.UseWindowsService(options =>
    options.ServiceName = "VoxOthers.Runtime");

// -----------------------------------------------------------------------------
// Logging
// -----------------------------------------------------------------------------
// IncludeScopes ligado: é o que carrega o BatchId em todo log do processamento
// sem repetir o campo em cada chamada. Sem isso, rastrear um item exige juntar
// log na mão — que é exatamente a dor do sistema atual.
builder.Logging.ClearProviders();
// Console no mesmo formato do arquivo: [yyyyMMdd-HHmmss.fff] [NIVEL] categoria: msg.
// O SimpleConsoleFormatter não deixa configurar o template da linha; por isso um
// formatter próprio, para console e arquivo saírem idênticos.
builder.Logging.AddConsole(options =>
{
    options.FormatterName = "vox";
});
builder.Logging.AddConsoleFormatter<VoxConsoleFormatter, ConsoleFormatterOptions>(options =>
{
    options.IncludeScopes = true;
});

// -----------------------------------------------------------------------------
// File log
// -----------------------------------------------------------------------------
// The console disappears when the process runs as a Windows service — the
// file log keeps log-based validation in both modes. One file per day, in
// the same format as the console lines (Logging:File in appsettings).
builder.Logging.AddFileLog();


// -----------------------------------------------------------------------------
// Configuração e entrada de dados
// -----------------------------------------------------------------------------
builder.Services.AddRuntimeConfiguration(builder.Configuration);

// Antes da ingestão: a fila se contabiliza, então depende dos indicadores.
builder.Services.AddObservability();

builder.Services.AddIngestion();

// Prazo de encerramento. O padrão do host é 5 segundos, tempo que não dá para
// esvaziar a fila — e lote recebido por webhook só existe em memória. Com dois
// minutos, o desligamento comum termina o que estava aceito; passando disso, o
// corte acontece e fica registrado em log qual foi o custo.
builder.Services.Configure<HostOptions>(options =>
    options.ShutdownTimeout = TimeSpan.FromMinutes(2));

// Pipeline de processamento (Fase 3)
// -----------------------------------------------------------------------------
builder.Services.AddProcessingPipeline(builder.Configuration);

// -----------------------------------------------------------------------------
// Health checks
// -----------------------------------------------------------------------------
// Dois endpoints com propósitos distintos:
//   live  -> o processo está de pé (não consulta dependência externa; se
//            consultasse, um banco lento reiniciaria um serviço saudável)
//   ready -> o serviço consegue efetivamente trabalhar agora
//
// As três conferem os três jeitos de o serviço parar de importar sem cair:
// pasta de entrada some, base do Vox não responde, destino do bilhete fica
// inacessível. Nenhum derruba o processo — é justamente por isso que precisam
// ser perguntados de fora.
builder.Services.AddHealthChecks()
    .AddCheck<IngestionPathsHealthCheck>("ingestion-paths", tags: ["ready"])
    .AddCheck<VoxDatabaseHealthCheck>("vox-database", tags: ["ready"])
    .AddCheck<GrfOutputHealthCheck>("grf-output", tags: ["ready"]);

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = Detalhar
});

// Descrição do problema junto do resultado. Sem isso o monitorador recebe
// "Unhealthy" e nada mais, e quem for atender tem de ir ao log do serviço
// descobrir qual das três conferências falhou.
static Task Detalhar(HttpContext contexto, HealthReport relatorio)
{
    contexto.Response.ContentType = "application/json";

    return contexto.Response.WriteAsJsonAsync(new
    {
        situacao = relatorio.Status.ToString(),
        verificacoes = relatorio.Entries.Select(e => new
        {
            nome = e.Key,
            situacao = e.Value.Status.ToString(),
            detalhe = e.Value.Description
        })
    });
}

app.MapCentralizeWebhook();

// O botão de reprocessar a quarentena. Vai ao ar mesmo com o webhook
// desligado: quem entra por pasta também precisa recuperar o que falhou.
app.MapQuarantineEndpoints();

// Acompanhamento: situação do serviço, rastro de um atendimento e indicadores.
app.MapDiagnostics();

// -----------------------------------------------------------------------------
// Registro de inicialização
// -----------------------------------------------------------------------------
// Só é alcançado depois que a validação de configuração passou. Serve como
// confirmação visível, no log, de com qual configuração o serviço subiu.
// O coletor precisa existir ANTES do primeiro item, e não na primeira consulta
// a /metrics. Ele é quem escuta as medições; criado sob demanda, tudo o que
// tivesse acontecido antes já teria passado sem ninguém do outro lado, e o
// indicador começaria do zero no momento em que alguém resolvesse olhar — bem
// quando o histórico é o que interessa.
app.Services.GetRequiredService<MetricsRegistry>();

var logger = app.Services.GetRequiredService<ILogger<Program>>();
var runtimeOptions = app.Services.GetRequiredService<IOptions<RuntimeOptions>>().Value;
var ingestionOptions = app.Services.GetRequiredService<IOptions<IngestionOptions>>().Value;

logger.Here().Info(
    "Vox Others Runtime iniciando. Workers={WorkerCount}, CapacidadeDaFila={ChannelCapacity}, " +
    "EntradaPorPasta={FolderEnabled}, EntradaPorWebhook={WebhookEnabled}, JornadaAtiva={WorkingHoursEnabled}",
    runtimeOptions.WorkerCount,
    runtimeOptions.ChannelCapacity,
    ingestionOptions.Folder.Enabled,
    ingestionOptions.Webhook.Enabled,
    runtimeOptions.WorkingHours.Enabled);

if (ingestionOptions.Webhook.Enabled && !ingestionOptions.Webhook.RequireApiKey)
{
    logger.Here().Warn(
        "O webhook está no ar SEM chave de acesso. Qualquer um que alcance {Caminho} " +
        "consegue injetar gravação no Vox. Use isso apenas em desenvolvimento.",
        ingestionOptions.Webhook.Path);
}

app.Run();

/// <summary>
/// Exposto para permitir testes de integração ponta a ponta.
/// </summary>
public partial class Program;
