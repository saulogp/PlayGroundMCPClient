using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
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
            yield return new ErrorEvent("LLM nao configurada. Va em /settings e preencha Model + ApiKey.");
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

        builder.AddOpenAIChatCompletion(
            modelId: llm.Model,
            apiKey: llm.ApiKey);

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

                var kernelFunctions = tools.Select(t => t.AsKernelFunction()).ToList();
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}

internal sealed class ToolCallObserverFilter(Channel<ChatStreamEvent> channel) : IFunctionInvocationFilter
{
    public async Task OnFunctionInvocationAsync(
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, Task> next)
    {
        // Coerce stringified primitives back to JSON-typed values BEFORE the call.
        // The OpenAI/MCP plumbing has been observed to round-trip booleans through
        // bool.ToString() ("True"/"False"), which then ships to the MCP server as
        // a string and breaks any tool that expects a boolean.
        NormalizeArguments(context.Arguments);

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

    private static void NormalizeArguments(KernelArguments args)
    {
        foreach (var key in args.Names.ToList())
        {
            var v = args[key];
            var coerced = CoerceValue(v);
            if (!ReferenceEquals(v, coerced))
            {
                args[key] = coerced;
            }
        }
    }

    private static object? CoerceValue(object? value)
    {
        switch (value)
        {
            case string s:
                if (s == "True") return true;
                if (s == "False") return false;
                return s;
            case JsonElement el:
                return el.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    JsonValueKind.Number => el.TryGetInt64(out var i) ? i : el.GetDouble(),
                    JsonValueKind.String => el.GetString(),
                    _ => value
                };
            default:
                return value;
        }
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
