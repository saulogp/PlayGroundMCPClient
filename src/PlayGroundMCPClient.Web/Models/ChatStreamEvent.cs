namespace PlayGroundMCPClient.Web.Models;

public abstract record ChatStreamEvent;

public sealed record TokenEvent(string Text) : ChatStreamEvent;

public sealed record ToolCallStartedEvent(
    string PluginName,
    string FunctionName,
    string ArgumentsJson) : ChatStreamEvent;

public sealed record ToolCallCompletedEvent(
    string PluginName,
    string FunctionName,
    string ResultJson,
    TimeSpan Elapsed,
    bool IsError) : ChatStreamEvent;

public sealed record DoneEvent(string FullText) : ChatStreamEvent;

public sealed record ErrorEvent(string Message) : ChatStreamEvent;
