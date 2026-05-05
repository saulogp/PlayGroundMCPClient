using System.Collections.Concurrent;
using System.Text.Json;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// File-backed library of LLM personalities. Persisted as
/// `personalities.json` in the content root. Same access pattern as
/// McpServerRegistry, with a Changed event so the UI can refresh.
public sealed class PersonalityRegistry
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<Guid, Personality> _store = new();

    public event Action? Changed;

    public PersonalityRegistry(IConfiguration config, IHostEnvironment env)
    {
        _filePath = Path.Combine(env.ContentRootPath, config["PersonalitiesFile"] ?? "personalities.json");
        Load();
    }

    public IReadOnlyList<Personality> GetAll() =>
        _store.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Personality? Get(Guid id) => _store.TryGetValue(id, out var p) ? p : null;

    public Personality? GetByName(string name) =>
        _store.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Upsert(Personality personality)
    {
        if (personality.Id == Guid.Empty) personality.Id = Guid.NewGuid();
        personality.UpdatedAt = DateTimeOffset.UtcNow;
        _store[personality.Id] = personality;
        Persist();
        Changed?.Invoke();
    }

    public bool Remove(Guid id)
    {
        var removed = _store.TryRemove(id, out _);
        if (removed)
        {
            Persist();
            Changed?.Invoke();
        }
        return removed;
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<Personality>>(json, JsonOpts);
            if (list is null) return;
            foreach (var p in list)
            {
                if (p.Id == Guid.Empty) p.Id = Guid.NewGuid();
                _store[p.Id] = p;
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
            var payload = _store.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            File.WriteAllText(_filePath, json);
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
