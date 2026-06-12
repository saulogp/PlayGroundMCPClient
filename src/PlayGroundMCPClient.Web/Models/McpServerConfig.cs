namespace PlayGroundMCPClient.Web.Models;

public enum McpServerSource
{
    File,
    Ui
}

public enum McpAuthMode
{
    None,
    BearerStatic,
    OAuth
}

public sealed class McpOAuthConfig
{
    public string? AuthorizationEndpoint { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? RegistrationEndpoint { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string Scopes { get; set; } = "";
    public string? Audience { get; set; }
    /// Empty = derive "{appBaseUrl}/oauth/callback" from the browser's address.
    /// Set explicitly only to override (must point back at this app's callback).
    public string RedirectUri { get; set; } = "";
    public bool UseDynamicClientRegistration { get; set; } = true;
}

public sealed class McpServerConfig
{
    public required string Name { get; init; }
    public required string Url { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public bool EnabledByDefault { get; init; }
    public McpServerSource Source { get; init; } = McpServerSource.File;
    public McpAuthMode AuthMode { get; set; } = McpAuthMode.None;
    public McpOAuthConfig? OAuth { get; set; }
}

public sealed class McpServersOptions
{
    public List<McpServerConfig> Servers { get; init; } = new();
}
