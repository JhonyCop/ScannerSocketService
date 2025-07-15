using ScannerWebSocketFormService.Models;

namespace ScannerWebSocketFormService.Services.Interface;

public interface IImageProcessor : IDisposable
{
    event EventHandler<ImageProcessedEventArgs>? ImageProcessed;
    int PageCount { get; }
    
    Task<bool> ProcessImageFromFileAsync(string filePath);
    Task SendPagesViaWebSocketAsync();
    void ClearPages();
    Task CancelScanAsync();
    void ResetCancelFlag();
}