using System.Text.Json;
using PlayGroundMCPClient.Web.Models;

namespace PlayGroundMCPClient.Web.Services;

/// DelegatingHandler that captures the JSON-RPC payload going OUT to an MCP
/// server (request body). Inbound frames are captured via the ILogger pipe in
/// the SDK because Streamable HTTP responses can be SSE streams that we must
/// not consume here.
public sealed class McpLoggingHandler(ProtocolLogStore store, string serverName) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Content is not null)
        {
            try
            {
                var body = await request.Content.ReadAsStringAsync(cancellationToken);
                var method = TryExtractMethod(body);
                store.Add(new ProtocolFrame(
                    DateTimeOffset.UtcNow,
                    serverName,
                    FrameDirection.Outbound,
                    method,
                    Pretty(body)));
            }
            catch
            {
                // never fail the request because of logging
            }
        }

        var response = await base.SendAsync(request, cancellationToken);

        // Only buffer when the server returned a complete JSON response. SSE
        // streams (text/event-stream) must be left untouched for the SDK.
        var ct = response.Content.Headers.ContentType?.MediaType;
        if (ct is "application/json")
        {
            try
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                store.Add(new ProtocolFrame(
                    DateTimeOffset.UtcNow,
                    serverName,
                    FrameDirection.Inbound,
                    null,
                    Pretty(body)));

                // Re-create content so downstream consumers can read it again.
                var newContent = new StringContent(body);
                foreach (var h in response.Content.Headers)
                {
                    newContent.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
                response.Content = newContent;
            }
            catch
            {
                // ignore
            }
        }
        else
        {
            store.Add(new ProtocolFrame(
                DateTimeOffset.UtcNow,
                serverName,
                FrameDirection.Info,
                null,
                $"<<response status={(int)response.StatusCode} content-type={ct ?? "(none)"}>>"));
        }

        return response;
    }

    private static string? TryExtractMethod(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("method", out var m) ? m.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Pretty(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return json;
        }
    }
}
