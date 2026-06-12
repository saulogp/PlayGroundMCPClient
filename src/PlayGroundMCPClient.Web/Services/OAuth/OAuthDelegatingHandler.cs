using System.Net;
using System.Net.Http.Headers;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services.OAuth;

/// Sits in front of every request to an OAuth-secured MCP server:
/// - Injects the current access token.
/// - Proactively refreshes when expiring soon.
/// - On 401, attempts a single refresh + retry; if that fails, marks the
///   server as needing re-authentication and surfaces the original 401.
public sealed class OAuthDelegatingHandler : DelegatingHandler
{
    private static readonly TimeSpan RefreshThreshold = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private readonly McpServerConfig _server;
    private readonly TokenStore _tokenStore;
    private readonly OAuthClient _oauthClient;
    private readonly OAuthState _state;
    private readonly ILogger _logger;

    public OAuthDelegatingHandler(
        McpServerConfig server,
        TokenStore tokenStore,
        OAuthClient oauthClient,
        OAuthState state,
        ILogger logger)
    {
        _server = server;
        _tokenStore = tokenStore;
        _oauthClient = oauthClient;
        _state = state;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = _tokenStore.Get(_server.Name)
            ?? throw new InvalidOperationException(
                $"Server '{_server.Name}' não autenticado. Execute o fluxo OAuth pela UI.");

        if (token.IsExpiringSoon(RefreshThreshold) && !string.IsNullOrWhiteSpace(token.RefreshToken))
        {
            token = await TryRefreshAsync(token, ct) ?? token;
        }

        ApplyAuth(request, token);
        var response = await base.SendAsync(request, ct);

        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        if (string.IsNullOrWhiteSpace(token.RefreshToken)) return MarkReauthAndReturn(response);

        response.Dispose();
        var refreshed = await TryRefreshAsync(token, ct);
        if (refreshed is null)
        {
            return MarkReauthAndReturn(new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        var retry = await CloneRequestAsync(request, ct);
        ApplyAuth(retry, refreshed);
        return await base.SendAsync(retry, ct);
    }

    private async Task<OAuthToken?> TryRefreshAsync(OAuthToken current, CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var fresh = _tokenStore.Get(_server.Name);
            if (fresh is not null && fresh.AccessToken != current.AccessToken)
            {
                return fresh;
            }

            var refreshed = await _oauthClient.RefreshAsync(_server, current, ct);
            _tokenStore.Save(_server.Name, refreshed);
            _state.ClearRequiresReauth(_server.Name);
            return refreshed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh OAuth falhou para {Server}", _server.Name);
            _state.MarkRequiresReauth(_server.Name);
            return null;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private HttpResponseMessage MarkReauthAndReturn(HttpResponseMessage response)
    {
        _state.MarkRequiresReauth(_server.Name);
        return response;
    }

    private static void ApplyAuth(HttpRequestMessage request, OAuthToken token)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            string.IsNullOrWhiteSpace(token.TokenType) ? "Bearer" : token.TokenType,
            token.AccessToken);
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version
        };
        foreach (var h in source.Headers)
        {
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        if (source.Content is not null)
        {
            var bytes = await source.Content.ReadAsByteArrayAsync(ct);
            var content = new ByteArrayContent(bytes);
            foreach (var h in source.Content.Headers)
            {
                content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            clone.Content = content;
        }
        return clone;
    }
}

/// Tracks per-server "needs re-auth" flags so the UI can react.
public sealed class OAuthState
{
    private readonly HashSet<string> _requiresReauth = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public event Action<string>? ReauthRequired;
    public event Action<string>? ReauthCleared;

    public bool RequiresReauth(string serverName)
    {
        lock (_lock) return _requiresReauth.Contains(serverName);
    }

    public void MarkRequiresReauth(string serverName)
    {
        bool added;
        lock (_lock) added = _requiresReauth.Add(serverName);
        if (added) ReauthRequired?.Invoke(serverName);
    }

    public void ClearRequiresReauth(string serverName)
    {
        bool removed;
        lock (_lock) removed = _requiresReauth.Remove(serverName);
        if (removed) ReauthCleared?.Invoke(serverName);
    }
}
