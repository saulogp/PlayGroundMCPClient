using System.Collections.Concurrent;
using System.Text.Json;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services.OAuth;

/// Per-server OAuth token persistence (access + refresh + DCR creds).
/// Stored separately from mcp-servers.user.json so tokens never leak into the
/// server registry file (which the user may commit).
public sealed class TokenStore
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, OAuthToken> _tokens = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? TokenChanged;

    public TokenStore(IConfiguration config, IHostEnvironment env)
    {
        _filePath = Path.Combine(env.ContentRootPath, config["McpTokensFile"] ?? "mcp-tokens.json");
        Load();
    }

    public OAuthToken? Get(string serverName) =>
        _tokens.TryGetValue(serverName, out var t) ? Clone(t) : null;

    public void Save(string serverName, OAuthToken token)
    {
        _tokens[serverName] = Clone(token);
        Persist();
        TokenChanged?.Invoke(serverName);
    }

    public bool Remove(string serverName)
    {
        var removed = _tokens.TryRemove(serverName, out _);
        if (removed)
        {
            Persist();
            TokenChanged?.Invoke(serverName);
        }
        return removed;
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var map = JsonSerializer.Deserialize<Dictionary<string, OAuthToken>>(json, JsonOpts);
            if (map is null) return;
            foreach (var (k, v) in map)
            {
                _tokens[k] = v;
            }
        }
        catch
        {
            // best effort
        }
    }

    private void Persist()
    {
        lock (_writeLock)
        {
            var json = JsonSerializer.Serialize(
                _tokens.ToDictionary(kv => kv.Key, kv => kv.Value),
                JsonOpts);
            File.WriteAllText(_filePath, json);
        }
    }

    private static OAuthToken Clone(OAuthToken t) => new()
    {
        AccessToken = t.AccessToken,
        RefreshToken = t.RefreshToken,
        ExpiresAt = t.ExpiresAt,
        TokenType = t.TokenType,
        Scope = t.Scope,
        ClientId = t.ClientId,
        ClientSecret = t.ClientSecret
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
