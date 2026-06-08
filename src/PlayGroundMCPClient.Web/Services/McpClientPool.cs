using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using PlayGroundMCPClient.Web.Models;
using PlayGroundMCPClient.Web.Services.OAuth;

namespace PlayGroundMCPClient.Web.Services;

/// Caches one IMcpClient per server name. Cache is invalidated whenever the
/// registry signals a change so URL/header updates take effect on next use.
public sealed class McpClientPool : IAsyncDisposable
{
    private readonly McpServerRegistry _registry;
    private readonly ProtocolLogStore _logStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TokenStore _tokenStore;
    private readonly OAuthClient _oauthClient;
    private readonly OAuthState _oauthState;
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new(StringComparer.OrdinalIgnoreCase);

    public McpClientPool(
        McpServerRegistry registry,
        ProtocolLogStore logStore,
        ILoggerFactory loggerFactory,
        TokenStore tokenStore,
        OAuthClient oauthClient,
        OAuthState oauthState)
    {
        _registry = registry;
        _logStore = logStore;
        _loggerFactory = loggerFactory;
        _tokenStore = tokenStore;
        _oauthClient = oauthClient;
        _oauthState = oauthState;
        _registry.Changed += InvalidateAll;
    }

    public Task<McpClient> GetAsync(McpServerConfig config) =>
        _clients.GetOrAdd(config.Name, _ => new Lazy<Task<McpClient>>(() => CreateAsync(config))).Value;

    public async Task InvalidateOneAsync(string serverName)
    {
        if (!_clients.TryRemove(serverName, out var lazy)) return;
        if (!lazy.IsValueCreated) return;
        try
        {
            var client = await lazy.Value;
            await client.DisposeAsync();
        }
        catch
        {
            // best effort
        }
    }

    private async Task<McpClient> CreateAsync(McpServerConfig config)
    {
        HttpMessageHandler chain = new HttpClientHandler();
        chain = new McpLoggingHandler(_logStore, config.Name) { InnerHandler = chain };

        if (config.AuthMode == McpAuthMode.OAuth)
        {
            chain = new OAuthDelegatingHandler(
                config,
                _tokenStore,
                _oauthClient,
                _oauthState,
                _loggerFactory.CreateLogger<OAuthDelegatingHandler>())
            {
                InnerHandler = chain
            };
        }

        var http = new HttpClient(chain);

        foreach (var (k, v) in config.Headers)
        {
            if (config.AuthMode == McpAuthMode.OAuth &&
                string.Equals(k, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
        }

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = new Uri(config.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            Name = config.Name
        };

        var transport = new HttpClientTransport(transportOptions, http, _loggerFactory, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport, loggerFactory: _loggerFactory);
    }

    private void InvalidateAll()
    {
        var snapshot = _clients.ToArray();
        _clients.Clear();
        _ = Task.Run(async () =>
        {
            foreach (var kv in snapshot)
            {
                try
                {
                    if (kv.Value.IsValueCreated)
                    {
                        var client = await kv.Value.Value;
                        await client.DisposeAsync();
                    }
                }
                catch
                {
                    // best effort
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        _registry.Changed -= InvalidateAll;
        foreach (var kv in _clients)
        {
            if (kv.Value.IsValueCreated)
            {
                try { await (await kv.Value.Value).DisposeAsync(); } catch { }
            }
        }
        _clients.Clear();
    }
}
