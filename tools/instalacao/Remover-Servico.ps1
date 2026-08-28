<#
.SYNOPSIS
  Remove o serviço Vox Others Runtime 2.0 do Windows.
.DESCRIPTION
  Equivalente moderno do "-Uninstall" do Others antigo. Para o serviço e o
  remove. Executar como Administrador.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Remover-Servico.ps1
#>
[CmdletBinding()]
param(
    [string]$ServiceName = 'VoxOthers.Runtime'
)

$ErrorActionPreference = 'Stop'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Write-Host "Servico '$ServiceName' nao existe — nada a remover."
    return
}

Write-Host "Parando '$ServiceName'..."
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

Write-Host "Removendo '$ServiceName'..."
sc.exe delete $ServiceName
if ($LASTEXITCODE -eq 0) {
    Write-Host "Servico removido."
} else {
    Write-Host "sc.exe delete retornou codigo $LASTEXITCODE."
}
