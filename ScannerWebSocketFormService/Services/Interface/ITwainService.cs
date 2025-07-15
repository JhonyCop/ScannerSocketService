using ScannerWebSocketFormService.Models;
using NTwain;

namespace ScannerWebSocketFormService.Services.Interface;

public interface ITwainService : IDisposable
{
    bool IsInitialized { get; }
    bool IsScanning { get; }
    
    event EventHandler<DataTransferredEventArgs>? DataTransferred;
    event EventHandler? SourceDisabled;
    event EventHandler? TransferReady;
    
    Task InitializeAsync();
    Task<List<ScannerDevice>> GetAvailableDevicesAsync();
    Task<bool> StartScanAsync();
    Task<bool> StartScanAsync(ScannerDevice device);
    void StopScan();
    
    // Métodos agregados para manejo de suspensión/reanudación
    Task ForceCleanStateAsync();
    Task ReinitializeAfterResumeAsync();
}