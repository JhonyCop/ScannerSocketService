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
        
        // Configurar detección de suspensión del sistema
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
                    await Task.Delay(3000); // Esperar 3 segundos para estabilización
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

            // Cerrar fuente actual de forma segura
            ClosePreviousSource();
            
            // Cerrar sesión TWAIN de forma segura
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
            
            // Limpiar estado completamente
            await ForceCleanState();
            
            // Reinicializar TWAIN
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
            // Detener timer de timeout si existe
            _timeoutTimer?.Dispose();
            _timeoutTimer = null;

            // Limpiar estado de escaneo
            _isScanning = false;
            _scanStartTime = null;

            // Cerrar fuente anterior
            ClosePreviousSource();

            // Limpiar sesión TWAIN
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

            // Notificar al WebSocket que el estado se ha limpiado
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
            // Limpiar cualquier sesión anterior
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
    
    
    //Obtener Dispositivos conectados o instalados pero activos 
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
                        // Intentar abrir la fuente para validar si está conectada
                        source.Open();
                        source.Close(); // Cerrar inmediatamente si no da error

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


    /*public async Task<List<ScannerDevice>> GetAvailableDevicesAsync()
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
                    devices.Add(new ScannerDevice
                    {
                        Id = source.Name,
                        Name = source.Name,
                        DisplayName = $"TWAIN-{source.Name}",
                        Type = ScannerType.TWAIN,
                        NativeDevice = source
                    });
                }
            }

            _logger.LogInformation("Encontrados {Count} dispositivos TWAIN", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error obteniendo dispositivos TWAIN");
            
            // Intentar reinicializar TWAIN en caso de error
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
    }*/

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
            
            // Configurar timeout de escaneo
            SetupScanTimeout();
            
            ClosePreviousSource();

            // Verificar y reabrir sesión TWAIN si es necesario
            await EnsureTwainSessionOpen();

            var src = _twain.ShowSourceSelector();
            if (src is null)
            {
                _logger.LogInformation("Usuario canceló la selección del escáner");
                await CleanupAfterCancel();
                return false;
            }

            _currentSource = src;
            src.Open();

            // Configurar scanner
            await ConfigureScanner(src);

            _logger.LogInformation("Abriendo interfaz del escáner...");
            src.Enable(SourceEnableMode.ShowUI, true, _windowHandle);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al inicializar escáner");
            await CleanupAfterError();
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

            // Refrescar la lista de dispositivos
            var sources = _twain.GetSources(); 
            if (!sources.Any())
            {
                _logger.LogWarning("No se encontraron fuentes TWAIN disponibles. ¿El escáner está desconectado?");
                await CleanupAfterError();
                return false;
            }

            var selectedSource = sources.FirstOrDefault(s => s.Name == device.Id);
            if (selectedSource == null)
            {
                _logger.LogError("No se encontró el dispositivo TWAIN: {DeviceId}", device.Id);
                await CleanupAfterError();
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
                await CleanupAfterError();
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
                await CleanupAfterError();
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inesperado al inicializar escáner específico");
            await CleanupAfterError();
            return false;
        }
    }


    private void SetupScanTimeout()
    {
        // Limpiar timer anterior si existe
        _timeoutTimer?.Dispose();
        
        // Configurar nuevo timer de timeout
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

    private async Task CleanupAfterCancel()
    {
        _isScanning = false;
        _scanStartTime = null;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
        
        await CloseTwainSession();
        await _webSocketService.ForceResetScanningState("Escaneo TWAIN cancelado por usuario");
    }

    private async Task CleanupAfterError()
    {
        _isScanning = false;
        _scanStartTime = null;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
        
        await CloseTwainSession();
        await _webSocketService.ForceResetScanningState("Error en escaneo TWAIN");
    }

    private async Task CleanupAfterTimeout()
    {
        _logger.LogWarning("Limpiando escaneo TWAIN por timeout");
        
        StopScan();
        await _webSocketService.ForceResetScanningState("Timeout de escaneo TWAIN");
        
        // Intentar reinicializar TWAIN
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
        
        // Limpiar estado
        _isScanning = false;
        _scanStartTime = null;
        _timeoutTimer?.Dispose();
        _timeoutTimer = null;
        
        ClosePreviousSource();
        _ = CloseTwainSession();
        
        // Notificar al WebSocket que el escaneo terminó
        await _webSocketService.ForceResetScanningState("Escaneo TWAIN completado");
        
        SourceDisabled?.Invoke(sender, e);
    }

    // Métodos públicos para manejo de suspensión/reanudación
    public async Task ForceCleanStateAsync()
    {
        await ForceCleanState();
    }

    public async Task ReinitializeAfterResumeAsync()
    {
        try
        {
            await ForceCleanState();
            await Task.Delay(1000); // Esperar estabilización
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