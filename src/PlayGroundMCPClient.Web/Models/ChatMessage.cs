namespace PlayGroundMCPClient.Web.Models;

public enum ChatRole
{
    User,
    Assistant,
    System,
    Tool
}

public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChatSessionId { get; set; }
    public ChatRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public string? ToolCallsJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ChatSession? ChatSession { get; set; }
}
