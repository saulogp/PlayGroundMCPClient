using System.Collections.Concurrent;
using ModelContextProtocol.Client;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// Caches one IMcpClient per server name. Cache is invalidated whenever the
/// registry signals a change so URL/header updates take effect on next use.
public sealed class McpClientPool : IAsyncDisposable
{
    private readonly McpServerRegistry _registry;
    private readonly ProtocolLogStore _logStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients = new(StringComparer.OrdinalIgnoreCase);

    public McpClientPool(McpServerRegistry registry, ProtocolLogStore logStore, ILoggerFactory loggerFactory)
    {
        _registry = registry;
        _logStore = logStore;
        _loggerFactory = loggerFactory;
        _registry.Changed += InvalidateAll;
    }

    public Task<McpClient> GetAsync(McpServerConfig config) =>
        _clients.GetOrAdd(config.Name, _ => new Lazy<Task<McpClient>>(() => CreateAsync(config))).Value;

    private async Task<McpClient> CreateAsync(McpServerConfig config)
    {
        var http = new HttpClient(new McpLoggingHandler(_logStore, config.Name)
        {
            InnerHandler = new HttpClientHandler()
        });
        foreach (var (k, v) in config.Headers)
        {
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
