# Vox Others Runtime

Runtime da nova geração do Vox Others. Consome dados já normalizados no formato
`CentralizeEntity` e importa para o Vox.

**Não** contém lógica de integração: nada de API, FTP, SFTP, banco de terceiro ou
fabricante. Essa responsabilidade é do Builder e dos backends que ele gera.

## Estrutura

| Projeto | Função |
|---|---|
| `src/VoxOthers.Contracts` | Contrato `CentralizeEntity`. Publicado como NuGet para os backends. Sem dependências. |
| `src/VoxOthers.Runtime` | Serviço: host, ingestão, pipeline, persistência e entrega. |
| `tests/VoxOthers.Tests` | Testes unitários e de integração. |

A arquitetura completa, com as decisões justificadas, está em
[`docs/arquitetura.md`](docs/arquitetura.md).

## Rodando

```powershell
dotnet build
dotnet test
dotnet run --project src/VoxOthers.Runtime
```

Verificação de saúde:

- `GET /health/live` — o processo está de pé
- `GET /health/ready` — o serviço consegue trabalhar (pastas acessíveis)

## Configuração

Editar `src/VoxOthers.Runtime/appsettings.json`. Configuração inválida **impede o
serviço de subir**, com mensagem indicando a chave e o valor incorretos — é
proposital: erro de configuração deve aparecer no boot, não de madrugada.

Segredos (credenciais, tokens) **nunca** vão no `appsettings.json` — use variável
de ambiente ou user secrets.

## Estado atual

Fase 0 concluída: base do projeto, configuração validada, logging e health checks.
As fases seguintes estão no JumpJump, no quadro Desenvolvimento (épico #22218).
