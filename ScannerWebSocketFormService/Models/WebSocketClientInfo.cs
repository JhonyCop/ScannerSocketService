using System.Net.WebSockets;

namespace ScannerWebSocketFormService.Models;

public class WebSocketClientInfo
{
    public string Id { get; set; } = string.Empty;
    public WebSocket WebSocket { get; set; } = null!;
    public DateTime ConnectedAt { get; set; }
    public DateTime LastActivity { get; set; }
    public string IpAddress { get; set; } = string.Empty;
}