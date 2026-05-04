using System.Text.Json;
using Microsoft.Extensions.Options;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// Holds the Azure OpenAI settings used by ChatOrchestrator.
/// Initial values come from appsettings.json, but can be overridden at runtime
/// from the UI and persisted to azure-openai.user.json (gitignored).
public sealed class AzureOpenAIStore
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private AzureOpenAIOptions _current;

    public event Action? Changed;

    public AzureOpenAIStore(IOptions<AzureOpenAIOptions> initial, IConfiguration config, IHostEnvironment env)
    {
        _filePath = Path.Combine(env.ContentRootPath, config["AzureOpenAIUserFile"] ?? "azure-openai.user.json");
        _current = Clone(initial.Value);
        LoadFromFile();
    }

    public AzureOpenAIOptions Current => _current;

    public void Update(AzureOpenAIOptions newValue)
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
            var opts = JsonSerializer.Deserialize<AzureOpenAIOptions>(json, JsonOpts);
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

    private static AzureOpenAIOptions Clone(AzureOpenAIOptions src) => new()
    {
        Endpoint = src.Endpoint,
        Deployment = src.Deployment,
        ApiKey = src.ApiKey,
        ApiVersion = src.ApiVersion
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
