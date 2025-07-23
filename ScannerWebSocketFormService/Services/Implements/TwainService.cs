using System.Reflection;
using Microsoft.Extensions.Logging;
using NTwain;
using ScannerWebSocketFormService.Services.Interface;
using ScannerWebSocketFormService.Models;
using NTwain.Data;
using Timer = System.Threading.Timer;

namespace ScannerWebSocketFormService.Services.Implements;

public class TwainService : ITwainService, IDisposable
{
    private readonly ILogger<TwainService> _logger;
    private readonly IntPtr _windowHandle;
    private readonly IWebSocketService _webSocketService;
    private TwainSession? _twain;
    private DataSource? _currentSource;
    private bool _isScanning = false;
    private bool _disposed = false;
    private DateTime? _scanStartTime = null;
    private readonly TimeSpan _scanTimeout = TimeSpan.FromMinutes(5);
    private Timer? _timeoutTimer;

    public event EventHandler<DataTransferredEventArgs>? DataTransferred;
    public event EventHandler? SourceDisabled;
    public event EventHandler? TransferReady;

    public bool IsInitialized => _twain != null;
    public bool IsScanning => _isScanning;

    public TwainService(ILogger<TwainService> logger, IntPtr windowHandle, IWebSocketService webSocketService)
    {
        _logger = logger;
        _windowHandle = windowHandle;
        _webSocketService = webSocketService;
        
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case Microsoft.Win32.PowerModes.Suspend:
                _logger.LogWarning("Sistema entrando en suspensión - Preparando TWAIN");
                HandleSystemSuspend();
                break;
                
            case Microsoft.Win32.PowerModes.Resume:
                _logger.LogInformation("Sistema reanudando - Reinicializando TWAIN");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    await HandleSystemResume();
                });
                break;
        }
    }

    private void HandleSystemSuspend()
    {
        try
        {
            if (_isScanning)
            {
                _logger.LogWarning("Limpiando escaneo activo debido a suspensión del sistema");
                StopScan();
            }

            ClosePreviousSource();
            
            try
            {
                if (_twain != null && _twain.IsDsmOpen)
                {
                    _twain.Close();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cerrando TWAIN durante suspensión");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando suspensión del sistema en TWAIN");
        }
    }

    private async Task HandleSystemResume()
    {
        try
        {
            _logger.LogInformation("Reinicializando TWAIN después de reanudación del sistema");
            
            await ForceCleanState();
            await InitializeAsync();
            
            _logger.LogInformation("TWAIN reinicializado correctamente después de reanudación");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reinicializando TWAIN después de reanudación");
        }
    }

    private async Task ForceCleanState()
    {
        try
        {
            _timeoutTimer?.Dispose();
            _timeoutTimer = null;

            _isScanning = false;
            _scanStartTime = null;

            ClosePreviousSource();

            try
            {
                if (_twain != null)
                {
                    if (_twain.IsDsmOpen)
                    {
                        _twain.Close();
                    }
                    _twain = null;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error limpiando sesión TWAIN");
            }

            await _webSocketService.ForceResetScanningState("TWAIN reinicializado después de suspensión");

            _logger.LogInformation("Estado de TWAIN limpiado completamente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en limpieza forzada de estado TWAIN");
        }
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (_twain != null)
            {
                try
                {
                    if (_twain.IsDsmOpen)
                    {
                        _twain.Close();
                    }
                }
                catch { }
                _twain = null;
            }

            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetExecutingAssembly());
            _twain = new TwainSession(appId);
            NTwain.PlatformInfo.Current.PreferNewDSM = true;

            _twain.TransferReady += OnTransferReady;
            _twain.DataTransferred += OnDataTransferred;
            _twain.SourceDisabled += OnSourceDisabled;

            _logger.LogInformation("TWAIN inicializado correctamente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inicializando TWAIN");
            throw;
        }
    }

    public async Task<List<ScannerDevice>> GetAvailableDevicesAsync()
    {
        var devices = new List<ScannerDevice>();

        try
        {
            if (_twain == null)
            {
                await InitializeAsync();
            }

            await EnsureTwainSessionOpen();

            if (_twain != null)
            {
                var sources = _twain.GetSources();
                foreach (var source in sources)
                {
                    if (source == null)
                        continue;

                    try
                    {
                        source.Open();
                        source.Close();

                        devices.Add(new ScannerDevice
                        {
                            Id = source.Name,
                            Name = source.Name,
                            DisplayName = $"TWAIN-{source.Name}",
                            Type = ScannerType.TWAIN,
                            NativeDevice = source
                        });
                    }
                    catch (Exception ex)
                    {
                        var sourceName = source?.Name ?? "NULL";
                        _logger.LogWarning(ex, "Dispositivo TWAIN ignorado (no disponible): {Source}", sourceName);
                        continue;
                    }
                }
            }

            _logger.LogInformation("Dispositivos TWAIN disponibles: {Count}", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo dispositivos TWAIN");

            try
            {
                await ForceCleanState();
                await InitializeAsync();
                return await GetAvailableDevicesAsync();
            }
            catch (Exception reinitEx)
            {
                _logger.LogError(reinitEx, "Error reinicializando TWAIN");
            }
        }

        return devices;
    }

    public async Task<bool> StartScanAsync()
    {
        if (_isScanning)
        {
            _logger.LogWarning("Ya hay un escaneo en progreso");
            return false;
        }

        if (_twain == null)
        {
            _logger.LogError("TWAIN no inicializado");
            return false;
        }

        try
        {
            _isScanning = true;
            _scanStartTime = DateTime.Now;
            
            SetupScanTimeout();
            ClosePreviousSource();

            await EnsureTwainSessionOpen();

            var src = _twain.ShowSourceSelector();
            if (src is null)
            {
                _logger.LogInformation("Usuario canceló la selección del escáner");
                await CleanupScan("Escaneo TWAIN cancelado por usuario");
                return false;
            }

            _currentSource = src;
            src.Open();

            await ConfigureScanner(src);

            _logger.LogInformation("Abriendo interfaz del escáner...");
            src.Enable(SourceEnableMode.ShowUI, true, _windowHandle);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al inicializar escáner");
            await CleanupScan("Error en escaneo TWAIN");
            return false;
        }
    }

    public async Task<bool> StartScanAsync(ScannerDevice device)
    {
        if (_isScanning)
        {
            _logger.LogWarning("Ya hay un escaneo en progreso");
            return false;
        }

        if (_twain == null)
        {
            _logger.LogError("TWAIN no inicializado");
            return false;
        }

        if (device.Type != ScannerType.TWAIN)
        {
            _logger.LogError("Dispositivo no es compatible con TWAIN");
            return false;
        }

        try
        {
            _isScanning = true;
            _scanStartTime = DateTime.Now;

            SetupScanTimeout();
            ClosePreviousSource();

            await EnsureTwainSessionOpen();

            var sources = _twain.GetSources(); 
            if (!sources.Any())
            {
                _logger.LogWarning("No se encontraron fuentes TWAIN disponibles. ¿El escáner está desconectado?");
                await CleanupScan("Error en escaneo TWAIN");
                return false;
            }

            var selectedSource = sources.FirstOrDefault(s => s.Name == device.Id);
            if (selectedSource == null)
            {
                _logger.LogError("No se encontró el dispositivo TWAIN: {DeviceId}", device.Id);
                await CleanupScan("Error en escaneo TWAIN");
                return false;
            }

            _currentSource = selectedSource;

            try
            {
                selectedSource.Open();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "No se pudo abrir la fuente del escáner. Posiblemente esté desconectado.");
                await CleanupScan("Error en escaneo TWAIN");
                return false;
            }

            await ConfigureScanner(selectedSource);

            try
            {
                _logger.LogInformation("Iniciando escaneo con dispositivo: {DeviceName}", device.Name);
                selectedSource.Enable(SourceEnableMode.ShowUI, true, _windowHandle);
            }
            catch (NullReferenceException ex)
            {
                _logger.LogError(ex, "Falló la habilitación del escáner. Posiblemente esté desconectado.");
                await CleanupScan("Error en escaneo TWAIN");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al inicializar escáner específico");
            await CleanupScan("Error en escaneo TWAIN");
            return false;
        }
    }

    private void SetupScanTimeout()
    {
        _timeoutTimer?.Dispose();
        
        _timeoutTimer = new Timer(async _ =>
        {
            if (_isScanning && _scanStartTime.HasValue)
            {
                var elapsed = DateTime.Now - _scanStartTime.Value;
                if (elapsed > _scanTimeout)
                {
                    _logger.LogWarning("Timeout de escaneo TWAIN alcanzado ({Minutes} minutos)", _scanTimeout.TotalMinutes);
                    await CleanupAfterTimeout();
                }
            }
        }, null, _scanTimeout, TimeSpan.FromMinutes(1));
    }

    private async Task CleanupScan(string message)
    {
        _isScanning = false;
        _scanStartTime = null;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
        
        await CloseTwainSession();
        await _webSocketService.ForceResetScanningState(message);
    }

    private async Task CleanupAfterTimeout()
    {
        _logger.LogWarning("Limpiando escaneo TWAIN por timeout");
        
        StopScan();
        await _webSocketService.ForceResetScanningState("Timeout de escaneo TWAIN");
        
        try
        {
            await ForceCleanState();
            await InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reinicializando TWAIN después de timeout");
        }
    }

    public void StopScan()
    {
        _isScanning = false;
        _scanStartTime = null;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
        
        ClosePreviousSource();
    }

    private async Task ConfigureScanner(DataSource src)
    {
        try
        {
            src.Capabilities.ICapXferMech.SetValue(XferMech.File);
            _logger.LogInformation("Mecanismo de transferencia: ARCHIVO");

            src.Capabilities.ICapXResolution.SetValue(300f);
            src.Capabilities.ICapYResolution.SetValue(300f);
            _logger.LogInformation("Resolución: 300 DPI");

            src.Capabilities.ICapCompression.SetValue(CompressionType.Jpeg);
            _logger.LogInformation("Compresión: JPEG");

            src.Capabilities.ICapPixelType.SetValue(PixelType.RGB);
            _logger.LogInformation("Formato: RGB");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Advertencia configuración scanner");
        }
    }

    private async Task EnsureTwainSessionOpen()
    {
        try
        {
            if (_twain != null && !_twain.IsDsmOpen)
            {
                _twain.Open();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error abriendo TWAIN, reinicializando");
            try
            {
                _twain?.Close();
            }
            catch { }
            
            await ForceCleanState();
            await InitializeAsync();
            
            if (_twain != null)
            {
                _twain.Open();
            }
        }
    }

    private async Task CloseTwainSession()
    {
        try
        {
            if (_twain != null && _twain.IsDsmOpen)
            {
                _twain.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cerrando sesión TWAIN");
        }
    }

    private void ClosePreviousSource()
    {
        try
        {
            if (_currentSource != null)
            {
                _logger.LogInformation("Cerrando fuente anterior...");
                try
                {
                    _currentSource.Close();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error cerrando fuente");
                }
                _currentSource = null;
                _logger.LogInformation("Fuente anterior cerrada correctamente");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error cerrando fuente anterior");
        }
    }

    private void OnTransferReady(object? sender, EventArgs e)
    {
        _logger.LogInformation("Página lista para transferir");
        TransferReady?.Invoke(sender, e);
    }

    private void OnDataTransferred(object? sender, DataTransferredEventArgs e)
    {
        DataTransferred?.Invoke(sender, e);
    }

    private async void OnSourceDisabled(object? sender, EventArgs e)
    {
        _logger.LogInformation("ESCANEO FINALIZADO");
        
        _isScanning = false;
        _scanStartTime = null;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
        
        ClosePreviousSource();
        _ = CloseTwainSession();
        
        await _webSocketService.ForceResetScanningState("Escaneo TWAIN completado");
        
        SourceDisabled?.Invoke(sender, e);
    }

    public async Task ForceCleanStateAsync()
    {
        await ForceCleanState();
    }

    public async Task ReinitializeAfterResumeAsync()
    {
        try
        {
            await ForceCleanState();
            await Task.Delay(1000);
            await InitializeAsync();
            _logger.LogInformation("TWAIN reinicializado exitosamente después de reanudación");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reinicializando TWAIN después de reanudación");
            throw;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }

            _timeoutTimer?.Dispose();
            _timeoutTimer = null;
            
            ClosePreviousSource();
            
            try
            {
                if (_twain != null)
                {
                    if (_twain.IsDsmOpen)
                    {
                        _twain.Close();
                    }
                    _twain = null;
                    _logger.LogInformation("TWAIN cerrado y liberado correctamente");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error cerrando TWAIN");
            }
            
            _disposed = true;
        }
    }
}