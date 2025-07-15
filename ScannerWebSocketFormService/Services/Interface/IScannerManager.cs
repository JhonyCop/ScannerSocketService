using ScannerWebSocketFormService.Models;

namespace ScannerWebSocketFormService.Services.Interface;

public interface IScannerManager
{
    Task<List<ScannerDevice>> GetAllUpdatedDevicesAsync(bool forceRefresh = true);
    Task<bool> StartScanAsync(ScannerDevice device);
    bool IsScanning { get; }
    Task<List<ScannerDevice>> RefreshDevicesForUI();
    Task<List<ScannerDevice>> ForceRefreshAllDevices();
    Task<List<ScannerDevice>> ForceRefreshAllDevicesClean(bool forceRefresh = true);
    Task<bool> QuickConnectivityCheckAsync(ScannerDevice device);
    void ClearConnectivityCache();
    

}