using System.Net.Http.Headers;
using System.Text.Json;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services.OAuth;

/// Implements RFC 9728 (Protected Resource Metadata) + RFC 8414 (Authorization
/// Server Metadata) discovery as required by the MCP Authorization spec.
public sealed class OAuthMetadataDiscovery
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<OAuthMetadataDiscovery> _logger;

    public OAuthMetadataDiscovery(IHttpClientFactory httpFactory, ILogger<OAuthMetadataDiscovery> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public async Task EnsureEndpointsAsync(McpServerConfig server, CancellationToken ct = default)
    {
        var oauth = server.OAuth ??= new McpOAuthConfig();
        if (!string.IsNullOrWhiteSpace(oauth.AuthorizationEndpoint) &&
            !string.IsNullOrWhiteSpace(oauth.TokenEndpoint))
        {
            return;
        }

        var http = _httpFactory.CreateClient("oauth-discovery");
        var resourceUri = new Uri(server.Url);

        var authServerUrls = await DiscoverProtectedResourceAsync(http, resourceUri, ct);
        foreach (var asUrl in authServerUrls)
        {
            if (await TryFillFromAuthServerAsync(http, asUrl, oauth, ct))
            {
                return;
            }
        }

        // Fallback: try same-origin discovery on the resource's authority.
        var rootBase = new Uri(resourceUri.GetLeftPart(UriPartial.Authority));
        if (await TryFillFromAuthServerAsync(http, rootBase, oauth, ct))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Não foi possível descobrir os endpoints OAuth para '{server.Name}'. " +
            "Informe AuthorizationEndpoint e TokenEndpoint manualmente.");
    }

    private async Task<List<Uri>> DiscoverProtectedResourceAsync(HttpClient http, Uri resourceUri, CancellationToken ct)
    {
        var found = new List<Uri>();

        // 1) Tentar 401 challenge para extrair resource_metadata.
        try
        {
            using var probe = new HttpRequestMessage(HttpMethod.Post, resourceUri)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            };
            using var probeResp = await http.SendAsync(probe, ct);
            if (probeResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                foreach (var h in probeResp.Headers.WwwAuthenticate)
                {
                    var metaUri = ExtractResourceMetadataUri(h);
                    if (metaUri is not null)
                    {
                        var meta = await FetchProtectedResourceMetadataAsync(http, metaUri, ct);
                        found.AddRange(meta);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probe 401 falhou para {Url}", resourceUri);
        }

        // 2) Tentar well-known padrão.
        if (found.Count == 0)
        {
            var wellKnown = BuildWellKnown(resourceUri, "/.well-known/oauth-protected-resource");
            var meta = await FetchProtectedResourceMetadataAsync(http, wellKnown, ct);
            found.AddRange(meta);
        }

        return found;
    }

    private async Task<List<Uri>> FetchProtectedResourceMetadataAsync(HttpClient http, Uri metaUri, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(metaUri, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("authorization_servers", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
            {
                return new();
            }
            var list = new List<Uri>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.GetString() is { } s && Uri.TryCreate(s, UriKind.Absolute, out var u))
                {
                    list.Add(u);
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao ler protected-resource metadata em {Url}", metaUri);
            return new();
        }
    }

    private async Task<bool> TryFillFromAuthServerAsync(HttpClient http, Uri authServerBase, McpOAuthConfig oauth, CancellationToken ct)
    {
        foreach (var path in new[] { "/.well-known/oauth-authorization-server", "/.well-known/openid-configuration" })
        {
            var url = BuildWellKnown(authServerBase, path);
            try
            {
                using var resp = await http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                oauth.AuthorizationEndpoint = GetStringOr(root, "authorization_endpoint", oauth.AuthorizationEndpoint);
                oauth.TokenEndpoint = GetStringOr(root, "token_endpoint", oauth.TokenEndpoint);
                oauth.RegistrationEndpoint = GetStringOr(root, "registration_endpoint", oauth.RegistrationEndpoint);

                if (!string.IsNullOrWhiteSpace(oauth.AuthorizationEndpoint) &&
                    !string.IsNullOrWhiteSpace(oauth.TokenEndpoint))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Falha em {Url}", url);
            }
        }
        return false;
    }

    private static string? GetStringOr(JsonElement root, string name, string? fallback) =>
        root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : fallback;

    private static Uri? ExtractResourceMetadataUri(AuthenticationHeaderValue header)
    {
        var param = header.Parameter;
        if (string.IsNullOrWhiteSpace(param)) return null;
        const string key = "resource_metadata=";
        var idx = param.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var raw = param[(idx + key.Length)..].Trim().TrimEnd(',').Trim();
        if (raw.StartsWith('"') && raw.EndsWith('"')) raw = raw[1..^1];
        return Uri.TryCreate(raw, UriKind.Absolute, out var u) ? u : null;
    }

    private static Uri BuildWellKnown(Uri origin, string path)
    {
        var b = new UriBuilder(origin) { Path = path, Query = "", Fragment = "" };
        return b.Uri;
    }
}
