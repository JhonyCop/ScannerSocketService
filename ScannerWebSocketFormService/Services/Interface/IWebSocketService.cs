using ScannerWebSocketFormService.Services.Interface;

namespace ScannerWebSocketFormService.Services.Interface;

public interface IWebSocketService : IDisposable
{
    bool IsListening { get; }
    int ConnectedClientsCount { get; }
    
    Task StartAsync();
    Task StopAsync();
    void RegisterScanHandler(Func<Task> scanHandler);
    void SetImageProcessor(IImageProcessor imageProcessor);
    Task BroadcastMessageAsync(object message);
    Task ForceResetScanningState(string reason);
    

    Task NotifyConnectivityCheckStarted(string deviceName);
    Task NotifyConnectivityCheckCompleted(string deviceName, bool isConnected, string details = "");
    Task NotifyDeviceSelectionError(string errorMessage);
}