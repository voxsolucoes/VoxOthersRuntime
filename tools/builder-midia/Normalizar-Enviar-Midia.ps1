# ============================================================================
#  Normalizar-Enviar-Midia.ps1  -  Builder minimo de gravacoes p/ Vox Others
# ----------------------------------------------------------------------------
#  Le as gravacoes .wav de uma pasta, normaliza os dados que vem no NOME DO
#  ARQUIVO e envia um lote (contrato CentralizeBatch) para o Vox Others
#  Runtime 2.0 via webhook.
#
#  Formato esperado do arquivo:
#      [OPERADOR]_RAMAL-NUMERO_AAAAMMDDHHMMSS(ID_FILA).wav
#  Ex.: [Wellington]_1313-01222997587826_20260629131124(4863).wav
#
#  Uso (exemplos):
#    .\Normalizar-Enviar-Midia.ps1                          # defaults
#    .\Normalizar-Enviar-Midia.ps1 -MidiaDir C:\Midia -DryRun
#    .\Normalizar-Enviar-Midia.ps1 -OperationId 45 -Source origem-x
#
#  ATENCAO: o Runtime COPIA a gravacao para a arvore de gravacao e depois
#  APAGA o arquivo de origem. Use -StagingDir para apontar para uma copia e
#  preservar os originais.
# ============================================================================

[CmdletBinding()]
param(
    [string]$MidiaDir   = 'C:\Simulacao\midia',
    [int]$ServerId      = 1,
    [int]$OperationId   = 1,
    [string]$Source     = '',                    # vazio => le do appsettings do Runtime
    [string]$ApiKey     = '',                    # vazio => le do appsettings do Runtime
    [string]$BaseUrl    = 'http://localhost:5000',
    [string]$WebhookPath = '/api/v1/centralize',
    [string]$RuntimeConfig = 'C:\DesenvVSO\VoxOthersRuntime\src\VoxOthers.Runtime\bin\Release\net10.0\appsettings.json',
    [string]$ExportJson = '',                    # se informado, grava o lote em .json
    [string]$StagingDir = '',                    # se informado, copia o .wav p/ ca e usa como mediaPath
    [int]$MaxBatch      = 500,
    [int]$DuracaoFallbackSegundos = 60,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Le chave/source do webhook direto da config do Runtime (sem expor credencial)
# ---------------------------------------------------------------------------
function Get-WebhookConfig {
    param([string]$ConfigPath)
    if (-not (Test-Path $ConfigPath)) { throw "Config do Runtime nao encontrada: $ConfigPath" }
    $raw = Get-Content $ConfigPath -Raw
    $clean = ($raw -split "`n" | Where-Object { $_ -notmatch '^\s*//' }) -join "`n"
    $j = $clean | ConvertFrom-Json
    $wh = $j.Ingestion.Webhook
    if (-not $wh -or -not $wh.Enabled) { throw "Webhook desabilitado na config do Runtime ($ConfigPath)." }
    if (-not $wh.ApiKeys) { throw "Sem ApiKeys na config do Runtime ($ConfigPath)." }
    $src = @($wh.ApiKeys.PSObject.Properties.Name)[0]
    $key = $wh.ApiKeys.$src
    return @{ Source = $src; ApiKey = $key; Path = $wh.Path }
}

# ---------------------------------------------------------------------------
# Duracao real de um .wav (header RIFF: byteRate e tamanho do chunk 'data')
# ---------------------------------------------------------------------------
function Get-WavDuracao {
    param([string]$Path)
    $fs = [System.IO.File]::OpenRead($Path)
    $br = New-Object System.IO.BinaryReader($fs)
    try {
        if ($fs.Length -lt 44) { return $null }
        if ([System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4)) -ne 'RIFF') { return $null }
        [void]$br.ReadInt32()
        if ([System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4)) -ne 'WAVE') { return $null }
        $byteRate = 0
        $dataSize = 0
        while ($fs.Position -lt $fs.Length) {
            if ($fs.Length - $fs.Position -lt 8) { break }
            $chunkId = [System.Text.Encoding]::ASCII.GetString($br.ReadBytes(4))
            $chunkSize = $br.ReadInt32()
            if ($chunkId -eq 'fmt ') {
                # ja leu audioFormat(2) + numChannels(2) + sampleRate(4) + byteRate(4) = 12 bytes;
                # pula o resto do payload (blockAlign + bitsPerSample [+ extras])
                [void]$br.ReadBytes(8)
                $byteRate = $br.ReadInt32()
                $rest = $chunkSize - 12
                if ($rest -gt 0) { [void]$br.ReadBytes($rest) }
            } elseif ($chunkId -eq 'data') {
                $dataSize = $chunkSize
                break
            } else {
                $skip = $chunkSize
                if (($chunkSize % 2) -eq 1) { $skip++ }
                [void]$br.ReadBytes($skip)
            }
        }
        if ($byteRate -gt 0) { return [math]::Round($dataSize / $byteRate, 0) }
        return $null
    } finally {
        $br.Close()
        $fs.Close()
    }
}

# ---------------------------------------------------------------------------
# Parse do nome: [OPERADOR]_RAMAL-NUMERO_AAAAMMDDHHMMSS(ID_FILA).ext
# ---------------------------------------------------------------------------
function Parse-NomeArquivo {
    param([string]$Nome)
    $m = [regex]::Match($Nome, '^\[(?<op>[^\]]+)\]_(?<ramal>\d+)-(?<num>\d+)_(?<dt>\d{14})\((?<id>\d+)\)\.(?<ext>\w+)$')
    if (-not $m.Success) { return $null }
    $dt = [DateTime]::ParseExact($m.Groups['dt'].Value, 'yyyyMMddHHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
    return [pscustomobject]@{
        Operador = $m.Groups['op'].Value
        Ramal    = $m.Groups['ramal'].Value
        Numero   = $m.Groups['num'].Value
        Inicio   = $dt
        IdFila   = $m.Groups['id'].Value
        Ext      = $m.Groups['ext'].Value
    }
}

# ---------------------------------------------------------------------------
# Monta um item do lote (camelCase, como o contrato espera)
# ---------------------------------------------------------------------------
function New-ItemLote {
    param($Info, $MediaPath, $Duracao, $ServerId, $OperationId)
    return @{
        uniqueId        = "MIDIA-$($Info.IdFila)"
        serverId        = $ServerId
        operationId     = $OperationId
        kind            = 'Call'
        agentName       = $Info.Operador
        extension       = $Info.Ramal
        ani             = $Info.Numero
        direction       = 'Unknown'
        startedAt       = $Info.Inicio.ToString('yyyy-MM-ddTHH:mm:sszzz')
        durationSeconds = $Duracao
        mediaPath       = $MediaPath
        extensions      = @{
            CALLID   = $Info.IdFila
            ID_FILA  = $Info.IdFila
            OPERADOR = $Info.Operador
            RAMAL    = $Info.Ramal
            NUMERO   = $Info.Numero
        }
    }
}

# ---------------------------------------------------------------------------
# Envia um lote pelo webhook e reporta
# ---------------------------------------------------------------------------
function Send-Batch {
    param($Batch, $Uri, $Source, $Key)
    $body = @{
        schemaVersion = 1
        source        = $Source
        generatedAt   = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        items         = $Batch
    } | ConvertTo-Json -Depth 8
    try {
        $resp = Invoke-WebRequest -Uri $Uri -Method Post -ContentType 'application/json' `
            -Headers @{ 'X-Api-Key' = $Key } -Body $body -UseBasicParsing
        Write-Host ("  -> HTTP " + $resp.StatusCode + "  " + $resp.Content) -ForegroundColor Green
    } catch {
        $r = $_.Exception.Response
        if ($r) {
            $code = [int]$r.StatusCode
            $rd = $r.GetResponseStream()
            $sr = New-Object System.IO.StreamReader($rd)
            $msg = $sr.ReadToEnd()
            Write-Host ("  -> HTTP " + $code + "  " + $msg) -ForegroundColor Red
        } else {
            Write-Host ("  -> ERRO: " + $_.Exception.Message) -ForegroundColor Red
        }
    }
}

# ---------------------------------------------------------------------------
# MAIN
# ---------------------------------------------------------------------------
Write-Host "== Builder Midia -> Vox Others Runtime ==" -ForegroundColor Cyan

if (-not (Test-Path $MidiaDir)) { throw "Pasta de midia nao encontrada: $MidiaDir" }
$arquivos = Get-ChildItem -Path $MidiaDir -Filter *.wav -File | Sort-Object Name
if ($arquivos.Count -eq 0) { Write-Host "Nenhum .wav em $MidiaDir"; exit 0 }
Write-Host ("Encontrados " + $arquivos.Count + " arquivos .wav em " + $MidiaDir)

# resolve config do webhook
$wh = Get-WebhookConfig -ConfigPath $RuntimeConfig
$useSource = if ($Source) { $Source } else { $wh.Source }
$useKey    = if ($ApiKey) { $ApiKey } else { $wh.ApiKey }
$usePath   = if ($WebhookPath) { $WebhookPath } else { $wh.Path }
$uri = $BaseUrl.TrimEnd('/') + $usePath
Write-Host ("Webhook: " + $uri + "  (source='" + $useSource + "')")

# staging opcional (preserva originais)
if ($StagingDir) {
    New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
    Write-Host ("Staging: copias em " + $StagingDir)
}

$itens = New-Object System.Collections.ArrayList
$erros = New-Object System.Collections.ArrayList

Write-Host ""
Write-Host ("{0,-12} {1,-8} {2,-16} {3,-16} {4,5}  {5}" -f 'OPERADOR','RAMAL','NUMERO','INICIO','DUR(s)','ID_FILA')

foreach ($f in $arquivos) {
    $info = Parse-NomeArquivo -Nome $f.Name
    if (-not $info) {
        [void]$erros.Add($f.Name)
        Write-Host ("  ! nome fora do padrao: " + $f.Name) -ForegroundColor Yellow
        continue
    }
    $mediaPath = $f.FullName
    if ($StagingDir) {
        $copia = Join-Path $StagingDir $f.Name
        Copy-Item -Path $f.FullName -Destination $copia -Force
        $mediaPath = $copia
    }
    $dur = Get-WavDuracao -Path $mediaPath
    if (-not $dur -or $dur -lt 1) {
        $dur = $DuracaoFallbackSegundos
        Write-Host ("  ? duracao nao lida, usando fallback " + $dur + "s: " + $f.Name) -ForegroundColor Yellow
    }
    $item = New-ItemLote -Info $info -MediaPath $mediaPath -Duracao $dur -ServerId $ServerId -OperationId $OperationId
    [void]$itens.Add($item)
    Write-Host ("{0,-12} {1,-8} {2,-16} {3,-16} {4,5}  {5}" -f $info.Operador, $info.Ramal, $info.Numero, $info.Inicio.ToString('dd/MM/yyyy HH:mm:ss'), $dur, $info.IdFila)
}

if ($itens.Count -eq 0) { Write-Host "Nenhum item valido para enviar."; exit 0 }
if ($erros.Count -gt 0) {
    Write-Host ""
    Write-Host ("AVISO: " + $erros.Count + " arquivo(s) ignorado(s) por nome fora do padrao:") -ForegroundColor Yellow
    $erros | ForEach-Object { Write-Host "  - $_" -ForegroundColor Yellow }
}

Write-Host ""
Write-Host ("Total de itens no lote: " + $itens.Count)

# opcao de exportar o JSON
if ($ExportJson) {
    $lote = @{
        schemaVersion = 1
        source        = $useSource
        generatedAt   = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
        items         = @($itens)
    } | ConvertTo-Json -Depth 8
    $lote | Out-File -FilePath $ExportJson -Encoding utf8
    Write-Host ("Lote exportado em: " + $ExportJson)
}

if ($DryRun) {
    Write-Host "DRY-RUN: nada foi enviado. Use o -ExportJson para ver o lote." -ForegroundColor Cyan
    exit 0
}

Write-Host ""
Write-Host ("Enviando lote (" + $itens.Count + " itens) para " + $uri + " ...")

# fatia em lotes de ate MaxBatch e envia
for ($i = 0; $i -lt $itens.Count; $i += $MaxBatch) {
    $fatia = @($itens[$i..([math]::Min($i + $MaxBatch - 1, $itens.Count - 1))])
    Write-Host ("  Lote " + [math]::Floor($i / $MaxBatch + 1) + ": " + $fatia.Count + " itens")
    Send-Batch -Batch $fatia -Uri $uri -Source $useSource -Key $useKey
}

Write-Host ""
Write-Host "Fim. Confira o resultado no log do Runtime (C:\Simulacao\Logs\VoxOthersRuntime\) e nas pastas de saida." -ForegroundColor Cyan