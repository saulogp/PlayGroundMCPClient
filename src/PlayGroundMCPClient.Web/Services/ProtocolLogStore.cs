using System.Collections.Concurrent;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// Singleton ring buffer for MCP protocol frames captured across all servers.
/// UI filters by server name; not partitioned per Blazor circuit in V1.
public sealed class ProtocolLogStore
{
    private const int Capacity = 500;
    private readonly ConcurrentQueue<ProtocolFrame> _frames = new();

    public event Action? Changed;

    public void Add(ProtocolFrame frame)
    {
        _frames.Enqueue(frame);
        while (_frames.Count > Capacity && _frames.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    public IReadOnlyList<ProtocolFrame> Snapshot() => _frames.ToArray();

    public void Clear()
    {
        while (_frames.TryDequeue(out _)) { }
        Changed?.Invoke();
    }
}
