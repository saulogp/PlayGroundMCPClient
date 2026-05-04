using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// Holds the merged list of MCP servers from `mcp-servers.json` (versioned)
/// and `mcp-servers.user.json` (UI additions, persisted on every change).
public sealed class McpServerRegistry
{
    private readonly IOptionsMonitor<McpServersOptions> _fileOptions;
    private readonly string _userFilePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, McpServerConfig> _userServers = new(StringComparer.OrdinalIgnoreCase);

    public event Action? Changed;

    public McpServerRegistry(IOptionsMonitor<McpServersOptions> fileOptions, IConfiguration config, IHostEnvironment env)
    {
        _fileOptions = fileOptions;
        _userFilePath = Path.Combine(env.ContentRootPath, config["McpServersUserFile"] ?? "mcp-servers.user.json");
        LoadUserFile();
        _fileOptions.OnChange(_ => Changed?.Invoke());
    }

    public IReadOnlyList<McpServerConfig> GetAll()
    {
        var fromFile = _fileOptions.CurrentValue.Servers
            .Select(s => new McpServerConfig
            {
                Name = s.Name,
                Url = s.Url,
                Headers = new Dictionary<string, string>(s.Headers),
                EnabledByDefault = s.EnabledByDefault,
                Source = McpServerSource.File
            });

        return fromFile.Concat(_userServers.Values).ToList();
    }

    public McpServerConfig? Get(string name) =>
        GetAll().FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    public void AddOrUpdate(McpServerConfig config)
    {
        var withSource = new McpServerConfig
        {
            Name = config.Name,
            Url = config.Url,
            Headers = config.Headers,
            EnabledByDefault = config.EnabledByDefault,
            Source = McpServerSource.Ui
        };
        _userServers[config.Name] = withSource;
        PersistUserFile();
        Changed?.Invoke();
    }

    public bool Remove(string name)
    {
        var removed = _userServers.TryRemove(name, out _);
        if (removed)
        {
            PersistUserFile();
            Changed?.Invoke();
        }
        return removed;
    }

    private void LoadUserFile()
    {
        if (!File.Exists(_userFilePath)) return;
        try
        {
            var json = File.ReadAllText(_userFilePath);
            var opts = JsonSerializer.Deserialize<McpServersOptions>(json, JsonOpts);
            if (opts is null) return;
            foreach (var s in opts.Servers)
            {
                _userServers[s.Name] = new McpServerConfig
                {
                    Name = s.Name,
                    Url = s.Url,
                    Headers = new Dictionary<string, string>(s.Headers),
                    EnabledByDefault = s.EnabledByDefault,
                    Source = McpServerSource.Ui
                };
            }
        }
        catch
        {
            // best effort
        }
    }

    private void PersistUserFile()
    {
        lock (_writeLock)
        {
            var payload = new McpServersOptions
            {
                Servers = _userServers.Values.ToList()
            };
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            File.WriteAllText(_userFilePath, json);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
