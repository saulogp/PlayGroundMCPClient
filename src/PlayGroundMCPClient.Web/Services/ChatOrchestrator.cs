using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using ModelContextProtocol.Client;
using PlayGroundMCPClient.Web.Data;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// Builds an SK Kernel per turn with only the MCP servers that are active for
/// the given chat session, then streams tokens and tool-call events back to
/// the UI through a channel.
public sealed class ChatOrchestrator(
    PlaygroundDbContext db,
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
        var session = await db.ChatSessions
            .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
        {
            session = new ChatSession { Id = sessionId, Title = Truncate(userMessage, 60) };
            db.ChatSessions.Add(session);
        }

        session.Messages.Add(new ChatMessage
        {
            ChatSessionId = session.Id,
            Role = ChatRole.User,
            Content = userMessage
        });
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

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
        foreach (var m in session.Messages)
        {
            switch (m.Role)
            {
                case ChatRole.User: history.AddUserMessage(m.Content); break;
                case ChatRole.Assistant: history.AddAssistantMessage(m.Content); break;
                case ChatRole.System: history.AddSystemMessage(m.Content); break;
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

        var fullText = sb.ToString();
        session.Messages.Add(new ChatMessage
        {
            ChatSessionId = session.Id,
            Role = ChatRole.Assistant,
            Content = fullText
        });
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);

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

    private static string SerializeArgs(KernelArguments args)
    {
        try
        {
            var dict = args.ToDictionary(k => k.Key, v => v.Value);
            return JsonSerializer.Serialize(dict, JsonOpts);
        }
        catch
        {
            return "{}";
        }
    }

    private static string SerializeResult(object? result)
    {
        if (result is null) return "null";
        if (result is string s) return s;
        try { return JsonSerializer.Serialize(result, JsonOpts); }
        catch { return result.ToString() ?? ""; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}
