using System.ComponentModel.DataAnnotations;

namespace PlayGroundMCPClient.Web.Models;

public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(200)]
    public string Title { get; set; } = "New chat";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string ActiveMcpServersJson { get; set; } = "[]";

    public List<ChatMessage> Messages { get; set; } = new();
}
