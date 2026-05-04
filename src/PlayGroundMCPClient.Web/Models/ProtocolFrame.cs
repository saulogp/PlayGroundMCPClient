namespace PlayGroundMCPClient.Web.Models;

public enum FrameDirection
{
    Outbound,
    Inbound,
    Info
}

public sealed record ProtocolFrame(
    DateTimeOffset Timestamp,
    string ServerName,
    FrameDirection Direction,
    string? Method,
    string Payload);
