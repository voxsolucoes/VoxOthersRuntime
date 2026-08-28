<#
.SYNOPSIS
  Instala o Vox Others Runtime 2.0 como serviço do Windows.
.DESCRIPTION
  Equivalente moderno do "-Install" do Others antigo (que usava InstallUtil).
  Registra com conta LocalSystem, start automático (inicia junto com o Windows),
  define a URL de escuta do serviço e o inicia.
  Executar como Administrador.

  A conexão com a base do Vox NÃO se configura aqui: edite o arquivo
  appsettings.json (seção "VoxDatabase") que fica na pasta do executável e
  reinicie o serviço. O Runtime monta a connection string a partir dos campos.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Instalar-Servico.ps1
  powershell -ExecutionPolicy Bypass -File .\Instalar-Servico.ps1 -Urls "http://0.0.0.0:5000"
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'VoxOthers.Runtime',
    [string]$DisplayName = 'Vox Others Runtime 2.0',
    [string]$Urls        = 'http://0.0.0.0:5000',
    [string]$ExePath = 'C:\DesenvVSO\VoxOthersRuntime\src\VoxOthers.Runtime\bin\Release\net10.0\VoxOthers.Runtime.exe'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) {
    throw "Executavel nao encontrado: $ExePath"
}

$jaExiste = [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)

if ($jaExiste) {
    Write-Host "Servico '$ServiceName' ja existe. Parando para atualizar variaveis..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
} else {
    Write-Host "Criando servico '$ServiceName' (LocalSystem, start automatico)..."
    # Caminho com espaco exige aspas no binPath, senão o SCM nao sobe o serviço.
    $bin = '"' + $ExePath + '"'
    New-Service -Name $ServiceName -BinaryPathName $bin -DisplayName $DisplayName -StartupType Automatic | Out-Null
    sc.exe description $ServiceName "Vox Others Runtime 2.0 - importacao de atendimentos normalizados (voz e chat) para o Vox."
}

# Variaveis de ambiente do servico: o Runtime as le no lugar do appsettings.
# Mescla com as existentes, para uma nova execucao nao apagar variaveis ja
# configuradas. Hoje o script so define a URL de escuta; a base do Vox fica
# inteiramente no appsettings.json (secao "VoxDatabase").
$regPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$vars = New-Object System.Collections.Generic.List[string]
$current = (Get-ItemProperty -Path $regPath -Name Environment -ErrorAction SilentlyContinue).Environment
if ($current) {
    foreach ($v in @($current)) { $vars.Add([string]$v) }
}
if ($Urls) {
    $var = "ASPNETCORE_URLS=$Urls"
    $key = 'ASPNETCORE_URLS'
    $achou = $false
    for ($i = 0; $i -lt $vars.Count; $i++) {
        if ($vars[$i] -like "$key=*") { $vars[$i] = $var; $achou = $true; break }
    }
    if (-not $achou) { $vars.Add($var) }
}
if ($vars.Count -gt 0) {
    Set-ItemProperty -Path $regPath -Name Environment -Value $vars.ToArray() -Type MultiString -ErrorAction Stop
    Write-Host "Variaveis do servico: $($vars -join ' ; ')"
}

Start-Service -Name $ServiceName
Start-Sleep -Seconds 5

$svc = Get-Service -Name $ServiceName
Write-Host ""
Write-Host ("Status: " + $svc.Status)
if ($svc.Status -eq 'Running') {
    $base = $Urls -replace '0\.0\.0\.0','127.0.0.1' -replace '\*','127.0.0.1'
    $health = $base.TrimEnd('/') + '/health/live'
    try {
        $live = Invoke-WebRequest -Uri $health -UseBasicParsing -TimeoutSec 5
        Write-Host ("$health -> HTTP " + $live.StatusCode + " " + $live.Content)
    } catch {
        Write-Host ("Nao consegui conferir $health : " + $_.Exception.Message)
    }
} else {
    Write-Host "Servico nao iniciou. Veja o Visualizador de Eventos (Windows Logs > Application) para o erro."
}
