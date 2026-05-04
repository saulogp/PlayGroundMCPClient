using System.Text.Json;
using Microsoft.Extensions.Options;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// Holds the LLM settings used by ChatOrchestrator.
/// Initial values come from appsettings.json (Llm section), overridable from
/// the UI and persisted to llm.user.json (gitignored).
public sealed class LlmStore
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private LlmOptions _current;

    public event Action? Changed;

    public LlmStore(IOptions<LlmOptions> initial, IConfiguration config, IHostEnvironment env)
    {
        _filePath = Path.Combine(env.ContentRootPath, config["LlmUserFile"] ?? "llm.user.json");
        _current = Clone(initial.Value);
        LoadFromFile();
    }

    public LlmOptions Current => _current;

    public void Update(LlmOptions newValue)
    {
        _current = Clone(newValue);
        PersistToFile();
        Changed?.Invoke();
    }

    private void LoadFromFile()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var opts = JsonSerializer.Deserialize<LlmOptions>(json, JsonOpts);
            if (opts is not null) _current = opts;
        }
        catch
        {
            // best effort — keep whatever was loaded from appsettings
        }
    }

    private void PersistToFile()
    {
        lock (_writeLock)
        {
            var json = JsonSerializer.Serialize(_current, JsonOpts);
            File.WriteAllText(_filePath, json);
        }
    }

    private static LlmOptions Clone(LlmOptions src) => new()
    {
        Model = src.Model,
        ApiKey = src.ApiKey
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
