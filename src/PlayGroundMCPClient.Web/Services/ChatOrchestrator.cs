using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using PlayGroundMCPClient.Web.Data;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// Builds an SK Kernel per turn with only the MCP servers that are active for
/// the given chat session, then streams tokens and tool-call events back to
/// the UI through a channel.
public sealed class ChatOrchestrator(
    IDbContextFactory<PlaygroundDbContext> dbFactory,
    McpClientPool pool,
    LlmStore llmStore,
    ILoggerFactory loggerFactory,
    ILogger<ChatOrchestrator> log)
{
    public async IAsyncEnumerable<ChatStreamEvent> SendAsync(
        Guid sessionId,
        string userMessage,
        IReadOnlyList<McpServerConfig> activeServers,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var llm = llmStore.Current;
        if (!llm.IsConfigured)
        {
            yield return new ErrorEvent(llm.Provider == LlmProvider.AzureOpenAI
                ? "LLM nao configurada. Va em /settings e preencha Endpoint + Deployment + ApiKey (Azure OpenAI)."
                : "LLM nao configurada. Va em /settings e preencha Model + ApiKey.");
            yield break;
        }

        var channel = Channel.CreateUnbounded<ChatStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var producer = Task.Run(async () =>
        {
            try
            {
                await ProduceAsync(sessionId, userMessage, activeServers, llm, channel, ct);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Chat orchestration failed");
                await channel.Writer.WriteAsync(new ErrorEvent(ex.Message), CancellationToken.None);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var ev in channel.Reader.ReadAllAsync(ct))
        {
            yield return ev;
        }

        await producer;
    }

    private async Task ProduceAsync(
        Guid sessionId,
        string userMessage,
        IReadOnlyList<McpServerConfig> activeServers,
        LlmOptions llm,
        Channel<ChatStreamEvent> channel,
        CancellationToken ct)
    {
        // Phase 1: ensure session exists, persist user message, snapshot history.
        // Never track an existing ChatSession as Modified — bumping UpdatedAt that
        // way intermittently produced a DbUpdateConcurrencyException ("0 rows
        // affected") on the second turn. Insert the new message and update the
        // timestamp via ExecuteUpdate so the change tracker only handles Added rows.
        List<(ChatRole Role, string Content)> historySnapshot;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var sessionExists = await db.ChatSessions
                .AsNoTracking()
                .AnyAsync(s => s.Id == sessionId, ct);

            if (!sessionExists)
            {
                db.ChatSessions.Add(new ChatSession
                {
                    Id = sessionId,
                    Title = Truncate(userMessage, 60)
                });
            }

            db.ChatMessages.Add(new ChatMessage
            {
                ChatSessionId = sessionId,
                Role = ChatRole.User,
                Content = userMessage
            });
            await db.SaveChangesAsync(ct);

            if (sessionExists)
            {
                await db.ChatSessions
                    .Where(s => s.Id == sessionId)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                        ct);
            }

            var rows = await db.ChatMessages
                .AsNoTracking()
                .Where(m => m.ChatSessionId == sessionId)
                .OrderBy(m => m.CreatedAt)
                .Select(m => new { m.Role, m.Content })
                .ToListAsync(ct);
            historySnapshot = rows.Select(r => (r.Role, r.Content)).ToList();
        }

        // Phase 2: build kernel + run streaming chat completion (no DB context held).
        var builder = Kernel.CreateBuilder();
        builder.Services.AddSingleton<IFunctionInvocationFilter>(
            new ToolCallObserverFilter(channel));
        builder.Services.AddSingleton(loggerFactory);

        if (llm.Provider == LlmProvider.AzureOpenAI)
        {
            builder.AddAzureOpenAIChatCompletion(
                deploymentName: llm.Model,
                endpoint: llm.Endpoint,
                apiKey: llm.ApiKey,
                apiVersion: string.IsNullOrWhiteSpace(llm.ApiVersion) ? null : llm.ApiVersion);
        }
        else
        {
            builder.AddOpenAIChatCompletion(
                modelId: llm.Model,
                apiKey: llm.ApiKey);
        }

        var kernel = builder.Build();

        foreach (var cfg in activeServers)
        {
            McpClient client;
            try
            {
                client = await pool.GetAsync(cfg);
            }
            catch (Exception ex)
            {
                await channel.Writer.WriteAsync(
                    new ErrorEvent($"Falha ao conectar no MCP '{cfg.Name}': {ex.Message}"), ct);
                continue;
            }

            try
            {
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                if (tools.Count == 0) continue;

                // Some MCP servers expose tool names with characters that the
                // OpenAI/Semantic Kernel function-name grammar rejects (only ASCII
                // letters, digits and underscores are allowed), e.g. Backstage's
                // 'techdocs-plugin.techdocs-search'. Rename the model-facing tool to a
                // sanitized form; WithName keeps the original name for the actual MCP
                // call, so no server-side mapping is needed. Dedupe to avoid clashes
                // when two raw names collapse to the same sanitized name.
                var usedNames = new HashSet<string>(StringComparer.Ordinal);
                var kernelFunctions = tools.Select(t =>
                {
                    var safeName = SanitizeToolName(t.Name, usedNames);
                    var tool = safeName == t.Name ? t : t.WithName(safeName);
                    return tool.AsKernelFunction();
                }).ToList();
                kernel.Plugins.AddFromFunctions(SanitizePluginName(cfg.Name), kernelFunctions);
            }
            catch (Exception ex)
            {
                await channel.Writer.WriteAsync(
                    new ErrorEvent($"Falha listando tools de '{cfg.Name}': {ex.Message}"), ct);
            }
        }

        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        foreach (var (role, content) in historySnapshot)
        {
            switch (role)
            {
                case ChatRole.User: history.AddUserMessage(content); break;
                case ChatRole.Assistant: history.AddAssistantMessage(content); break;
                case ChatRole.System: history.AddSystemMessage(content); break;
            }
        }

        var settings = new OpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto()
        };

        var sb = new StringBuilder();
        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct))
        {
            if (!string.IsNullOrEmpty(chunk.Content))
            {
                sb.Append(chunk.Content);
                await channel.Writer.WriteAsync(new TokenEvent(chunk.Content), ct);
            }
        }

        // Phase 3: persist assistant message in a fresh context. Use ExecuteUpdate
        // for the timestamp so we don't rely on tracking the session entity.
        var fullText = sb.ToString();
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            db.ChatMessages.Add(new ChatMessage
            {
                ChatSessionId = sessionId,
                Role = ChatRole.Assistant,
                Content = fullText
            });
            await db.ChatSessions
                .Where(s => s.Id == sessionId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow),
                    CancellationToken.None);
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await channel.Writer.WriteAsync(new DoneEvent(fullText), CancellationToken.None);
    }

    private static string SanitizePluginName(string name)
    {
        var clean = Regex.Replace(name, "[^a-zA-Z0-9_]", "_");
        return string.IsNullOrEmpty(clean) ? "mcp" : clean;
    }

    // Maps a raw MCP tool name onto the OpenAI/SK function-name grammar
    // (ASCII letters, digits and underscores only) and guarantees uniqueness
    // within the plugin so AddFromFunctions doesn't fail on a duplicate.
    private static string SanitizeToolName(string name, HashSet<string> used)
    {
        var clean = Regex.Replace(name, "[^a-zA-Z0-9_]", "_");
        if (string.IsNullOrEmpty(clean)) clean = "tool";

        var candidate = clean;
        var i = 1;
        while (!used.Add(candidate))
        {
            candidate = $"{clean}_{i++}";
        }
        return candidate;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}

internal sealed class ToolCallObserverFilter(Channel<ChatStreamEvent> channel) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        // Coerce stringified arguments back to JSON-typed values BEFORE the call.
        // Some models (and the OpenAI/MCP plumbing) emit every tool argument as a
        // string — e.g. limit "1" instead of 1, or filter "{\"Priority\":3}" instead
        // of an object — which then fails the server's JSON-schema validation. We use
        // each parameter's declared schema type (from the MCP tool's inputSchema) to
        // convert strings into the expected number/integer/boolean/object/array.
        NormalizeArguments(context.Arguments, context.Function.Metadata);

        var pluginName = context.Function.PluginName ?? "";
        var functionName = context.Function.Name;
        var args = SerializeArgs(context.Arguments);

        await channel.Writer.WriteAsync(
            new ToolCallStartedEvent(pluginName, functionName, args));

        var sw = Stopwatch.StartNew();
        try
        {
            await next(context);
            sw.Stop();
            var resultJson = SerializeResult(context.Result?.GetValue<object?>());
            await channel.Writer.WriteAsync(
                new ToolCallCompletedEvent(pluginName, functionName, resultJson, sw.Elapsed, false));
        }
        catch (Exception ex)
        {
            sw.Stop();
            await channel.Writer.WriteAsync(
                new ToolCallCompletedEvent(pluginName, functionName, ex.Message, sw.Elapsed, true));
            throw;
        }
    }

    private static void NormalizeArguments(KernelArguments args, KernelFunctionMetadata metadata)
    {
        var expectedTypes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in metadata.Parameters)
        {
            expectedTypes[p.Name] = GetSchemaType(p);
        }

        foreach (var key in args.Names.ToList())
        {
            var v = args[key];
            expectedTypes.TryGetValue(key, out var expectedType);
            var coerced = CoerceValue(v, expectedType);
            if (!ReferenceEquals(v, coerced))
            {
                args[key] = coerced;
            }
        }
    }

    // Reads the JSON-schema "type" for a parameter from the MCP tool's inputSchema.
    // The type may be a plain string ("number") or a nullable union (["number","null"]);
    // in the union case we return the first non-null type.
    private static string? GetSchemaType(KernelParameterMetadata p)
    {
        var root = p.Schema?.RootElement;
        if (root is not { ValueKind: JsonValueKind.Object } el) return null;
        if (!el.TryGetProperty("type", out var t)) return null;

        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in t.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && s != "null")
                {
                    return s;
                }
            }
        }
        return null;
    }

    private static object? CoerceValue(object? value, string? expectedType)
    {
        // Pull out the raw string when the argument arrived as a string (either a
        // CLR string or a JSON string element). Anything else is left to the legacy
        // JsonElement normalization below.
        var str = value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null
        };

        if (str is not null)
        {
            switch (expectedType)
            {
                case "object":
                case "array":
                    try { return JsonNode.Parse(str); }
                    catch { return value; }
                case "integer":
                    return long.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var l)
                        ? l : value;
                case "number":
                    return double.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)
                        ? d : value;
                case "boolean":
                    return bool.TryParse(str, out var b) ? b : value;
                default:
                    // No usable schema hint — keep the old "True"/"False" heuristic.
                    if (str == "True") return true;
                    if (str == "False") return false;
                    return str;
            }
        }

        // Non-string JsonElement: unwrap to a CLR value so it serializes with the
        // correct JSON type on the way to the MCP server.
        if (value is JsonElement other)
        {
            return other.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                JsonValueKind.Number => other.TryGetInt64(out var i) ? i : other.GetDouble(),
                _ => value
            };
        }

        return value;
    }

    private static string SerializeArgs(KernelArguments args)
    {
        try
        {
            var dict = args.ToDictionary(k => k.Key, v => v.Value);
            return PrettifyIfJson(JsonSerializer.Serialize(dict, JsonOpts));
        }
        catch
        {
            return "{}";
        }
    }

    private static string SerializeResult(object? result)
    {
        if (result is null) return "null";
        if (result is string s) return PrettifyIfJson(s);

        // MCP tool results often arrive as ContentBlock / IList<ContentBlock>.
        // Show the inner text directly instead of the protocol envelope.
        if (TryExtractContentText(result, out var text))
        {
            return PrettifyIfJson(text);
        }

        try { return JsonSerializer.Serialize(result, JsonOpts); }
        catch { return result.ToString() ?? ""; }
    }

    private static bool TryExtractContentText(object result, out string text)
    {
        if (result is TextContentBlock tb)
        {
            text = tb.Text ?? "";
            return true;
        }
        if (result is IEnumerable<ContentBlock> blocks)
        {
            var sb = new StringBuilder();
            foreach (var b in blocks)
            {
                if (b is TextContentBlock t && !string.IsNullOrEmpty(t.Text))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append(t.Text);
                }
            }
            if (sb.Length > 0)
            {
                text = sb.ToString();
                return true;
            }
        }
        text = "";
        return false;
    }

    private static string PrettifyIfJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var trimmed = raw.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '[')) return raw;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc.RootElement, JsonOpts);
        }
        catch
        {
            return raw;
        }
    }

    // UnsafeRelaxedJsonEscaping keeps " as \" and accents as themselves instead
    // of escaping to \u00XX, so tool-call cards in the chat read naturally.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
