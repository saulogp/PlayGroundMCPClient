# PlayGround MCP Client

Local Blazor Server playground to test your **StreamableHTTP MCP servers** against **Azure OpenAI** with full visibility (chat, tool inspector, JSON-RPC log).

## Stack

- .NET 10 + Blazor Server + MudBlazor
- Microsoft.SemanticKernel 1.75 + Connectors.AzureOpenAI
- ModelContextProtocol 1.2 (StreamableHTTP transport)
- EF Core SQLite (chat persistence)

## Run locally

1. Configure Azure OpenAI in `src/PlayGroundMCPClient.Web/appsettings.Development.json` (or via user-secrets):
   ```json
   {
     "AzureOpenAI": {
       "Endpoint": "https://YOUR-RESOURCE.openai.azure.com/",
       "Deployment": "gpt-4o",
       "ApiKey": "YOUR-KEY",
       "ApiVersion": "2024-10-21"
     }
   }
   ```
2. Edit `src/PlayGroundMCPClient.Web/mcp-servers.json` to point at your MCP servers (or add via UI).
3. Run:
   ```bash
   dotnet run --project src/PlayGroundMCPClient.Web
   ```
4. Open http://localhost:5188.

## Usar

- **Chat** (`/`): selecione MCPs ativos no chip bar, mande mensagem. Tool calls aparecem como cards expansíveis.
- **MCP Servers** (`/mcp-servers`): adicione servers via UI (vai para `mcp-servers.user.json`); use **Inspect** para ver tools/resources/prompts; use **Test** para validar conexão.
- **Protocol Log** (botão terminal no app bar): drawer lateral mostra frames JSON-RPC trafegados em tempo real, com filtro.

## Docker

```bash
docker build -t playground-mcp .
docker run -d -p 8080:8080 \
  -e AzureOpenAI__Endpoint=https://... \
  -e AzureOpenAI__Deployment=gpt-4o \
  -e AzureOpenAI__ApiKey=... \
  -v $PWD/data:/app/data \
  playground-mcp
```

## Arquivos chave

- `src/PlayGroundMCPClient.Web/Services/ChatOrchestrator.cs` — SK kernel + Azure + plugins MCP por chat.
- `src/PlayGroundMCPClient.Web/Services/McpClientPool.cs` — único ponto que fala StreamableHTTP.
- `src/PlayGroundMCPClient.Web/Services/McpLoggingHandler.cs` — captura JSON-RPC para o Protocol Log.
- `src/PlayGroundMCPClient.Web/mcp-servers.json` — config versionável de MCPs.
