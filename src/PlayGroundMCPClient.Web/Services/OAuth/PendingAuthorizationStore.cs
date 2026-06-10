using System.Collections.Concurrent;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services.OAuth;

/// One in-flight Authorization Code + PKCE request, kept server-side between
/// the moment the user opens the authorization URL and the moment the provider
/// redirects back to /oauth/callback. Keyed by the OAuth `state` value.
public sealed class PendingAuthorization
{
    public required string State { get; init; }
    public required string ServerName { get; init; }
    public required string CodeVerifier { get; init; }
    public required string RedirectUri { get; init; }
    public required McpOAuthConfig OAuth { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// Holds pending authorizations across the browser round-trip. The Blazor
/// circuit that starts the flow and the /oauth/callback HTTP request that
/// finishes it are different requests, so the PKCE verifier and resolved OAuth
/// config must be parked here (keyed by `state`) rather than in component state.
public sealed class PendingAuthorizationStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<string, PendingAuthorization> _pending =
        new(StringComparer.Ordinal);

    public void Add(PendingAuthorization auth)
    {
        Prune();
        _pending[auth.State] = auth;
    }

    /// Removes and returns the pending authorization for `state`, or null if it
    /// is unknown or expired.
    public PendingAuthorization? Consume(string state)
    {
        if (!_pending.TryRemove(state, out var auth)) return null;
        return DateTimeOffset.UtcNow - auth.CreatedAt > Ttl ? null : auth;
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - Ttl;
        foreach (var kv in _pending)
        {
            if (kv.Value.CreatedAt < cutoff)
            {
                _pending.TryRemove(kv.Key, out _);
            }
        }
    }
}
