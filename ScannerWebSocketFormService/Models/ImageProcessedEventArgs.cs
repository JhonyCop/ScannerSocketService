namespace ScannerWebSocketFormService.Models;

public class ImageProcessedEventArgs : EventArgs
{
    public int PageNumber { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public long MemoryUsageMB { get; set; }
}