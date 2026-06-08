namespace PlayGroundMCPClient.Web.Models;

public sealed class OAuthToken
{
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string TokenType { get; set; } = "Bearer";
    public string? Scope { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    public bool IsExpiringSoon(TimeSpan threshold) =>
        ExpiresAt.HasValue && ExpiresAt.Value - DateTimeOffset.UtcNow <= threshold;
}
