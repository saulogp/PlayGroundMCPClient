# PlayGround MCP Client

Local Blazor Server playground to test your **StreamableHTTP MCP servers** against an **OpenAI** LLM with full visibility (chat, tool inspector, JSON-RPC log).

## Stack

- .NET 10 + Blazor Server + MudBlazor
- Microsoft.SemanticKernel 1.75 (Connectors.OpenAI)
- ModelContextProtocol 1.2 (StreamableHTTP transport)
- EF Core SQLite (chat persistence)

## Run locally

1. `dotnet run --project src/PlayGroundMCPClient.Web`
2. Open http://localhost:5188.
3. Click **LLM Settings** in the nav, set **Model** (e.g. `gpt-4o`) and **API Key**, hit **Testar conexao** and **Salvar**.
4. Add your MCP servers in **MCP Servers** (or edit `mcp-servers.json`).

Settings persist locally in `llm.user.json` and `mcp-servers.user.json` (both gitignored).

You can also pre-configure via `appsettings.json` or env vars:
```bash
Llm__Model=gpt-4o Llm__ApiKey=sk-... dotnet run --project src/PlayGroundMCPClient.Web
```

## Usar

- **Chat** (`/`): selecione MCPs ativos nos chips, mande mensagem. Tool calls aparecem como cards expansiveis.
- **MCP Servers** (`/mcp-servers`): adicione servers via UI (vai para `mcp-servers.user.json`); use **Inspect** para ver tools/resources/prompts; use **Test** para validar conexao.
- **LLM Settings** (`/settings`): Model + ApiKey, com **Testar conexao**.
- **Protocol Log** (icone terminal no app bar): drawer lateral com frames JSON-RPC em tempo real, com filtro.

## Docker

```bash
docker build -t playground-mcp .
docker run -d -p 8080:8080 \
  -e Llm__Model=gpt-4o \
  -e Llm__ApiKey=sk-... \
  -v $PWD/data:/app/data \
  playground-mcp
```

## Arquivos chave

- `src/PlayGroundMCPClient.Web/Services/ChatOrchestrator.cs` — SK kernel + OpenAI + plugins MCP por chat.
- `src/PlayGroundMCPClient.Web/Services/McpClientPool.cs` — unico ponto que fala StreamableHTTP.
- `src/PlayGroundMCPClient.Web/Services/McpLoggingHandler.cs` — captura JSON-RPC para o Protocol Log.
- `src/PlayGroundMCPClient.Web/Services/LlmStore.cs` — singleton com Model/ApiKey, persiste em llm.user.json.
- `src/PlayGroundMCPClient.Web/mcp-servers.json` — config versionavel de MCPs.

## Licença

Distribuído sob a [PolyForm Noncommercial License 1.0.0](LICENSE) — uso, cópia e modificação permitidos **apenas para fins não comerciais**.

Required Notice: Copyright 2026 Saulo Proetti (sauloproetti@gmail.com)
