using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services.OAuth;

/// Drives the Authorization Code + PKCE flow in a web-native way (works both
/// locally and inside a container, where the app and the browser are on
/// different machines):
/// - (optional) Dynamic Client Registration
/// - PrepareAuthorizationAsync builds the authorization URL using this app's
///   own /oauth/callback as redirect_uri, and parks the PKCE state server-side.
/// - The user's own browser opens that URL; the provider redirects back to
///   /oauth/callback, which calls CompleteAuthorizationAsync to swap the code
///   for tokens.
/// - Refreshes tokens.
public sealed class OAuthClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly OAuthMetadataDiscovery _discovery;
    private readonly PendingAuthorizationStore _pending;
    private readonly ILogger<OAuthClient> _logger;

    public OAuthClient(
        IHttpClientFactory httpFactory,
        OAuthMetadataDiscovery discovery,
        PendingAuthorizationStore pending,
        ILogger<OAuthClient> logger)
    {
        _httpFactory = httpFactory;
        _discovery = discovery;
        _pending = pending;
        _logger = logger;
    }

    /// Resolves endpoints + client credentials, generates PKCE/state, parks the
    /// pending authorization keyed by state, and returns the authorization URL
    /// the user's browser should open. `redirectUri` must point back at this
    /// app's /oauth/callback and be reachable from the browser.
    public async Task<string> PrepareAuthorizationAsync(McpServerConfig server, string redirectUri, CancellationToken ct = default)
    {
        await _discovery.EnsureEndpointsAsync(server, ct);
        var oauth = server.OAuth ?? throw new InvalidOperationException("OAuth config ausente");

        var http = _httpFactory.CreateClient("oauth-discovery");
        await EnsureClientCredentialsAsync(http, server, redirectUri, ct);

        var verifier = GenerateCodeVerifier();
        var challenge = ComputeS256Challenge(verifier);
        var state = RandomBase64Url(16);

        var authUrl = BuildAuthorizationUrl(oauth, redirectUri, challenge, state);

        _pending.Add(new PendingAuthorization
        {
            State = state,
            ServerName = server.Name,
            CodeVerifier = verifier,
            RedirectUri = redirectUri,
            OAuth = CloneOAuth(oauth)
        });

        return authUrl;
    }

    /// Completes the flow from the /oauth/callback endpoint: matches the pending
    /// authorization by state, validates, and exchanges the code for tokens.
    public async Task<(string ServerName, OAuthToken Token)> CompleteAuthorizationAsync(
        string state, string? code, string? error, string? errorDescription, CancellationToken ct = default)
    {
        var pending = _pending.Consume(state)
            ?? throw new OAuthException("OAuth state inválido ou expirado", "");

        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new OAuthException($"OAuth error: {error} - {errorDescription}", error);
        }
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new OAuthException("OAuth callback sem 'code'", "");
        }

        var http = _httpFactory.CreateClient("oauth-discovery");
        var token = await ExchangeCodeAsync(http, pending.OAuth, code!, pending.RedirectUri, pending.CodeVerifier, ct);
        return (pending.ServerName, token);
    }

    public async Task<OAuthToken> RefreshAsync(McpServerConfig server, OAuthToken current, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(current.RefreshToken))
        {
            throw new InvalidOperationException("Sem refresh_token disponível");
        }
        await _discovery.EnsureEndpointsAsync(server, ct);
        var oauth = server.OAuth!;
        var http = _httpFactory.CreateClient("oauth-discovery");

        var clientId = current.ClientId ?? oauth.ClientId
            ?? throw new InvalidOperationException("ClientId ausente para refresh");

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = current.RefreshToken!,
            ["client_id"] = clientId
        };
        var secret = current.ClientSecret ?? oauth.ClientSecret;
        if (!string.IsNullOrWhiteSpace(secret))
        {
            form["client_secret"] = secret!;
        }
        if (!string.IsNullOrWhiteSpace(oauth.Scopes))
        {
            form["scope"] = oauth.Scopes;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, oauth.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new OAuthException($"Refresh falhou ({(int)resp.StatusCode}): {body}", body);
        }

        var refreshed = ParseTokenResponse(body);
        refreshed.RefreshToken ??= current.RefreshToken;
        refreshed.ClientId = clientId;
        refreshed.ClientSecret = secret;
        return refreshed;
    }

    private async Task EnsureClientCredentialsAsync(HttpClient http, McpServerConfig server, string redirectUri, CancellationToken ct)
    {
        var oauth = server.OAuth!;
        if (!string.IsNullOrWhiteSpace(oauth.ClientId)) return;
        if (!oauth.UseDynamicClientRegistration || string.IsNullOrWhiteSpace(oauth.RegistrationEndpoint))
        {
            throw new InvalidOperationException(
                "ClientId não informado e DCR desabilitado/não suportado. " +
                "Preencha ClientId no cadastro do server.");
        }

        var body = new
        {
            client_name = $"PlayGroundMCPClient - {server.Name}",
            redirect_uris = new[] { redirectUri },
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
            token_endpoint_auth_method = "none",
            application_type = "web",
            scope = oauth.Scopes
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, oauth.RegistrationEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new OAuthException($"DCR falhou ({(int)resp.StatusCode}): {json}", json);
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        oauth.ClientId = root.TryGetProperty("client_id", out var cid) ? cid.GetString() : null;
        oauth.ClientSecret = root.TryGetProperty("client_secret", out var cs) ? cs.GetString() : null;
        if (string.IsNullOrWhiteSpace(oauth.ClientId))
        {
            throw new OAuthException("DCR não retornou client_id", json);
        }
    }

    private async Task<OAuthToken> ExchangeCodeAsync(HttpClient http, McpOAuthConfig oauth, string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = oauth.ClientId!,
            ["code_verifier"] = verifier
        };
        if (!string.IsNullOrWhiteSpace(oauth.ClientSecret))
        {
            form["client_secret"] = oauth.ClientSecret!;
        }
        if (!string.IsNullOrWhiteSpace(oauth.Audience))
        {
            form["resource"] = oauth.Audience!;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, oauth.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new OAuthException($"Token exchange falhou ({(int)resp.StatusCode}): {body}", body);
        }
        var token = ParseTokenResponse(body);
        token.ClientId = oauth.ClientId;
        token.ClientSecret = oauth.ClientSecret;
        return token;
    }

    private static OAuthToken ParseTokenResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var token = new OAuthToken
        {
            AccessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "",
            RefreshToken = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            TokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer",
            Scope = root.TryGetProperty("scope", out var sc) ? sc.GetString() : null
        };
        if (root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds))
        {
            token.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            throw new OAuthException("Resposta sem access_token", json);
        }
        return token;
    }

    private static string BuildAuthorizationUrl(McpOAuthConfig oauth, string redirectUri, string challenge, string state)
    {
        var q = System.Web.HttpUtility.ParseQueryString(string.Empty);
        q["response_type"] = "code";
        q["client_id"] = oauth.ClientId!;
        q["redirect_uri"] = redirectUri;
        q["code_challenge"] = challenge;
        q["code_challenge_method"] = "S256";
        q["state"] = state;
        if (!string.IsNullOrWhiteSpace(oauth.Scopes)) q["scope"] = oauth.Scopes;
        if (!string.IsNullOrWhiteSpace(oauth.Audience)) q["resource"] = oauth.Audience;
        var sep = oauth.AuthorizationEndpoint!.Contains('?') ? '&' : '?';
        return $"{oauth.AuthorizationEndpoint}{sep}{q}";
    }

    private static McpOAuthConfig CloneOAuth(McpOAuthConfig o) => new()
    {
        AuthorizationEndpoint = o.AuthorizationEndpoint,
        TokenEndpoint = o.TokenEndpoint,
        RegistrationEndpoint = o.RegistrationEndpoint,
        ClientId = o.ClientId,
        ClientSecret = o.ClientSecret,
        Scopes = o.Scopes,
        Audience = o.Audience,
        RedirectUri = o.RedirectUri,
        UseDynamicClientRegistration = o.UseDynamicClientRegistration
    };

    private static string GenerateCodeVerifier()
    {
        var bytes = new byte[64];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string ComputeS256Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string RandomBase64Url(int byteCount)
    {
        var bytes = new byte[byteCount];
        RandomNumberGenerator.Fill(bytes);
        return Base64Url(bytes);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed class OAuthException : Exception
{
    public string Body { get; }
    public OAuthException(string message, string body) : base(message)
    {
        Body = body;
    }
}
