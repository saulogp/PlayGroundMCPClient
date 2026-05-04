namespace PlayGroundMCPClient.Web.Models;

public enum McpServerSource
{
    File,
    Ui
}

public sealed class McpServerConfig
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public bool EnabledByDefault { get; init; }
    public McpServerSource Source { get; init; } = McpServerSource.File;
}

public sealed class McpServersOptions
{
    public List<McpServerConfig> Servers { get; init; } = new();
}
