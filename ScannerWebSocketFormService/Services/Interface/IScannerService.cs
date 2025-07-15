using ScannerWebSocketFormService.Models;

namespace ScannerWebSocketFormService.Services.Interface;

public interface IScannerService : IDisposable
{

    event EventHandler<string>? ImageScanned;
    event EventHandler? ScanCompleted;
    event EventHandler<string>? ScanError;
    
    Task<bool> QuickConnectivityCheckAsync(ScannerDevice device);
    void ClearDeviceCache();
    

    event EventHandler<string>? QuickConnectivityCheckStarted;
    event EventHandler<string>? QuickConnectivityCheckFailed;
    event EventHandler<string>? DeviceConnectivityVerified;
    event EventHandler<string>? DeviceTimeout;
    event EventHandler? AutoRecoveryTriggered;
    
    bool IsInitialized { get; }
    bool IsScanning { get; }
    ScannerType ServiceType { get; }
    
    Task InitializeAsync();
    Task<List<ScannerDevice>> GetAvailableDevicesAsync();
    Task<bool> StartScanAsync(ScannerDevice device, bool showUI = false);
    void StopScan();
}