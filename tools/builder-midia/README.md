# Builder Midia → Vox Others Runtime 2.0

Mini sistema que **normaliza gravações cujos dados vêm no nome do arquivo** e
envia um lote (contrato `CentralizeBatch`) para o Vox Others Runtime via
webhook.

## Formato de arquivo reconhecido

```
[OPERADOR]_RAMAL-NUMERO_AAAAMMDDHHMMSS(ID_FILA).wav
```

| Parte | Exemplo | Vira no lote |
|---|---|---|
| `[OPERADOR]` | `[Wellington]` | `agentName` (o Runtime localiza/cadastra o usuário no Vox) |
| `RAMAL` | `1313` | `extension` |
| `NUMERO` | `01222997587826` | `ani` |
| `AAAAMMDDHHMMSS` | `20260629131124` | `startedAt` (ISO-8601 com offset local) |
| `ID_FILA` | `4863` | `uniqueId` (`MIDIA-4863`), `extensions.CALLID`, `extensions.ID_FILA` |

A **duração** não vem no nome: o builder lê do header RIFF do `.wav`
(byteRate × tamanho do chunk `data`).

## Como usar

```powershell
# Envio real (webhook). O Runtime COPIA a gravação e APAGA a origem.
.\Normalizar-Enviar-Midia.ps1

# Simular: só monta e mostra o lote, não envia
.\Normalizar-Enviar-Midia.ps1 -DryRun

# Ajustes comuns
.\Normalizar-Enviar-Midia.ps1 -OperationId 45 -ServerId 1
.\Normalizar-Enviar-Midia.ps1 -MidiaDir 'C:\OutraPasta' -StagingDir 'C:\Staging' -ExportJson 'lote.json'
```

Parâmetros principais:

| Parâmetro | Default | Observação |
|---|---|---|
| `-MidiaDir` | `C:\Simulacao\midia` | Pasta com os `.wav` |
| `-ServerId` | `1` | Servidor Vox de destino |
| `-OperationId` | `1` | Operação do Vox (precisa existir na base) |
| `-BaseUrl` | `http://localhost:5000` | Onde o Runtime escuta |
| `-WebhookPath` | `/api/v1/centralize` | Rota do webhook |
| `-RuntimeConfig` | `bin\Release\net10.0\appsettings.json` | Chave/source do webhook são lidos daqui (sem hardcode) |
| `-StagingDir` | vazio | Se preencher, copia o `.wav` para cá e usa como `mediaPath` (preserva os originais) |
| `-ExportJson` | vazio | Grava o lote em `.json` |
| `-DryRun` | off | Monta e mostra, não envia |
| `-MaxBatch` | `500` | Tamanho máximo de cada envio |

## Fluxo

1. Varre `-MidiaDir` por `*.wav` e faz o parse do nome.
2. Monta o `CentralizeBatch` (camelCase, enums em texto, `generatedAt` com offset).
3. `POST` em `{BaseUrl}{WebhookPath}` com `X-Api-Key` (lida do `appsettings.json` do Runtime).
4. HTTP **202** = aceito para processamento assíncrono (não é "importado").

## Observações

- O `source` do lote precisa bater com a origem da chave da config (default
  `builder-exemplo` no perfil Production).
- `uniqueId = MIDIA-<id_fila>` garante **deduplicação** (operationId + uniqueId):
  reenviar o mesmo arquivo vira "Duplicado", sem gravar de novo.
- O Runtime **copia** a gravação para a árvore (`Grav\yyyy\MM\dd\canal\`) e
  **apaga a origem**. Use `-StagingDir` se quiser manter os originais.
- Canal/prefixo do arquivo são decididos pelo Runtime (reutiliza o canal do
  operador na operação).

## Verificação após o envio

- Bilhetes: `C:\Simulacao\Grav\REGBD\*.GRF`
- Gravações: `C:\Simulacao\Grav\yyyy\MM\dd\<canal>\`
- Marcadores de dedup: `C:\Simulacao\ImportHistory\op-<operationId>\`
- Log do dia: `C:\Simulacao\Logs\VoxOthersRuntime\<yyyyMMdd>_VoxOthersRuntime.log`

Resultado do primeiro teste real (12/08/2026): lote de 33 itens → **33/33
importados**, zero quarentenados, canal 3223 reaproveitado.