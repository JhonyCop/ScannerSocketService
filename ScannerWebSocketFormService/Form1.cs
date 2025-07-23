using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NTwain;
using ScannerWebSocketFormService.Services.Implements;
using ScannerWebSocketFormService.Services.Interface;
using ScannerWebSocketFormService.Models;

namespace ScannerWebSocketFormService;

public partial class Form1 : Form
{
    #region Fields and Constants
    private readonly ILogger<Form1> _logger;
    private readonly ITwainService _twainService;
    private readonly WiaService _wiaService;
    private readonly IWebSocketService _webSocketService;
    private readonly ITempFileManager _tempFileManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly SystemStateManager _systemStateManager;
    private readonly ServiceProvider _serviceProvider;
    private readonly IScannerManager _scannerManager;
    
    private IScannerService? _currentScannerService;
    private DateTime? _lastScanAttempt = null;
    private DateTime? _lastScanError = null;
    private DateTime? _lastUserCancellation = null;
    
    private readonly TimeSpan _scanCooldown = TimeSpan.FromSeconds(2);
    private readonly TimeSpan _errorCooldown = TimeSpan.FromSeconds(4);
    private int _consecutiveCancellations = 0;
    private const int MAX_ALLOWED_CANCELLATIONS = 3;
    private bool _lastScanWasSuccessful = true;
    #endregion

    #region Constructor and Initialization
    public Form1()
    {
        InitializeComponent();
        
        _serviceProvider = ConfigureServices();
        
        _logger = _serviceProvider.GetRequiredService<ILogger<Form1>>();
        _twainService = _serviceProvider.GetRequiredService<ITwainService>();
        _wiaService = _serviceProvider.GetRequiredService<WiaService>();
        _webSocketService = _serviceProvider.GetRequiredService<IWebSocketService>();
        _tempFileManager = _serviceProvider.GetRequiredService<ITempFileManager>();
        _imageProcessor = _serviceProvider.GetRequiredService<IImageProcessor>();
        _systemStateManager = _serviceProvider.GetRequiredService<SystemStateManager>();
        _scannerManager = _serviceProvider.GetRequiredService<IScannerManager>();
        
        ConfigureEvents();
        ConfigureSystemStateHandlers();
        
        _ = InitializeAsync();
    }

    private ServiceProvider ConfigureServices()  
    {
        var services = new ServiceCollection();
    
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
    
        services.AddSingleton<ITempFileManager, TempFileManager>();
        services.AddSingleton<IWebSocketService, WebSocketService>();
        services.AddSingleton<SystemStateManager>();
        services.AddSingleton<ITwainService>(provider => 
            new TwainService(
                provider.GetRequiredService<ILogger<TwainService>>(), 
                this.Handle,
                provider.GetRequiredService<IWebSocketService>()));
    
        services.AddSingleton<WiaService, WiaService>();
        services.AddSingleton<IScannerService>(provider => provider.GetRequiredService<WiaService>());
        services.AddSingleton<IImageProcessor, ImageProcessor>();
        services.AddSingleton<IScannerManager, ScannerManager>();
    
        return services.BuildServiceProvider();
    }

    private async Task InitializeAsync()
    {
        try
        {
            ConfigureHiddenForm();
            _tempFileManager.Initialize();
            await _twainService.InitializeAsync();
            await _wiaService.InitializeAsync();
            await _webSocketService.StartAsync();
            
            var initialState = _systemStateManager.GetCurrentState();
            _logger.LogInformation("Aplicación inicializada correctamente - Estado del sistema: {State}", initialState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inicializando aplicación");
        }
    }
    #endregion

    #region Event Configuration
    private void ConfigureEvents()
    {
        // Eventos TWAIN
        _twainService.DataTransferred += OnTwainDataTransferred;
        _twainService.SourceDisabled += OnTwainSourceDisabled;
        _twainService.TransferReady += OnTwainTransferReady;

        // Eventos WIA
        _wiaService.ImageScanned += OnWiaImageScanned;
        _wiaService.ScanCompleted += OnWiaScanCompleted;
        _wiaService.ScanError += OnWiaScanError;

        // Eventos de verificación de conectividad WIA
        _wiaService.QuickConnectivityCheckStarted += OnQuickConnectivityCheckStarted;
        _wiaService.QuickConnectivityCheckFailed += OnQuickConnectivityCheckFailed;
        _wiaService.DeviceConnectivityVerified += OnDeviceConnectivityVerified;

        _webSocketService.SetImageProcessor(_imageProcessor);
        _webSocketService.RegisterScanHandler(HandleScanRequest);

        Load += OnFormLoad;
        FormClosed += OnFormClosed;
        FormClosing += OnFormClosing;
    }

    private void ConfigureSystemStateHandlers()
    {
        _systemStateManager.RegisterSuspendHandler(HandleSystemSuspend);
        _systemStateManager.RegisterResumeHandler(HandleSystemResume);
        _systemStateManager.RegisterSessionLockHandler(HandleSessionLock);
        _systemStateManager.RegisterSessionUnlockHandler(HandleSessionUnlock);

        _systemStateManager.PowerModeChanged += (sender, e) =>
        {
            _logger.LogInformation("Evento de modo de energía: {Mode}", e.Mode);
        };

        _systemStateManager.SessionSwitchOccurred += (sender, e) =>
        {
            _logger.LogInformation("Evento de cambio de sesión: {Reason}", e.Reason);
        };
    }
    #endregion

    #region Scan Request Handling
    private async Task HandleScanRequest()
    {
        if (ShouldBlockScanDueToCooldown())
        {
            await HandleScanCooldown();
            return;
        }

        _imageProcessor.ResetCancelFlag();

        this.Invoke(new Action(async () =>
        {
            try
            {
                var scanStarted = await ShowUnifiedDeviceSelectorAndScanAsync();
            
                if (!scanStarted)
                {
                    _logger.LogDebug("Escaneo no iniciado - estado ya manejado");
                }
            }
            catch (Exception ex)
            {
                await HandleScanError(ex, "Error en handler de escaneo");
            }
        }));
    }

    private async Task HandleScanCooldown()
    {
        var remainingTime = GetRemainingCooldownTime();
        string reason = GetCooldownReason();
        
        _logger.LogWarning("Escaneo bloqueado por cooldown - {RemainingSeconds}s restantes. Razón: {Reason}", 
            remainingTime.TotalSeconds, reason);
    
        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_cooldown",
            message = $"Espera {remainingTime.TotalSeconds:F0} segundos antes de escanear nuevamente",
            remainingSeconds = remainingTime.TotalSeconds,
            reason = reason,
            consecutiveCancellations = _consecutiveCancellations,
            maxAllowedCancellations = MAX_ALLOWED_CANCELLATIONS
        });

        await _webSocketService.ForceResetScanningState("Escaneo bloqueado por cooldown");
    }

    private string GetCooldownReason()
    {
        if (_consecutiveCancellations >= MAX_ALLOWED_CANCELLATIONS)
        {
            return $"Demasiadas cancelaciones consecutivas ({_consecutiveCancellations})";
        }
        else if (_lastScanWasSuccessful)
        {
            return "Límite de velocidad de escaneo";
        }
        else
        {
            return "Error reciente";
        }
    }

    private async Task HandleScanError(Exception ex, string context)
    {
        _logger.LogError(ex, context);
        _lastScanWasSuccessful = false;
        _lastScanError = DateTime.Now;

        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_error",
            message = $"Error inesperado: {ex.Message}"
        });

        await _webSocketService.ForceResetScanningState(context);
    }
    #endregion

    #region Cooldown Management
    private bool ShouldBlockScanDueToCooldown()
    {
        var now = DateTime.Now;

        // No aplicar cooldown por cancelaciones del usuario (hasta 3 veces)
        if (_consecutiveCancellations < MAX_ALLOWED_CANCELLATIONS)
        {
            _logger.LogDebug("Cancelaciones consecutivas: {Count}/{Max} - No aplicar cooldown", 
                _consecutiveCancellations, MAX_ALLOWED_CANCELLATIONS);
            return false;
        }

        // Cooldown por demasiadas cancelaciones consecutivas
        if (_consecutiveCancellations >= MAX_ALLOWED_CANCELLATIONS && _lastUserCancellation.HasValue)
        {
            var timeSinceLastCancellation = now - _lastUserCancellation.Value;
            if (timeSinceLastCancellation < _scanCooldown)
            {
                _logger.LogWarning("Demasiadas cancelaciones consecutivas ({Count}) - Aplicando cooldown", 
                    _consecutiveCancellations);
                return true;
            }
        }

        // Cooldown normal para escaneos exitosos
        if (_lastScanWasSuccessful && _lastScanAttempt.HasValue)
        {
            var timeSinceLastScan = now - _lastScanAttempt.Value;
            if (timeSinceLastScan < _scanCooldown)
            {
                return true;
            }
        }

        // Cooldown para errores técnicos
        if (!_lastScanWasSuccessful && _lastScanError.HasValue)
        {
            var timeSinceLastError = now - _lastScanError.Value;
            if (timeSinceLastError < _errorCooldown)
            {
                return true;
            }
        }

        return false;
    }

    private TimeSpan GetRemainingCooldownTime()
    {
        var now = DateTime.Now;

        // Cooldown por demasiadas cancelaciones
        if (_consecutiveCancellations >= MAX_ALLOWED_CANCELLATIONS && _lastUserCancellation.HasValue)
        {
            var timeSinceLastCancellation = now - _lastUserCancellation.Value;
            var remaining = _scanCooldown - timeSinceLastCancellation;
            if (remaining > TimeSpan.Zero)
                return remaining;
        }

        // Cooldown normal por escaneo exitoso
        if (_lastScanWasSuccessful && _lastScanAttempt.HasValue)
        {
            var timeSinceLastScan = now - _lastScanAttempt.Value;
            var remaining = _scanCooldown - timeSinceLastScan;
            if (remaining > TimeSpan.Zero)
                return remaining;
        }

        // Cooldown por error técnico
        if (!_lastScanWasSuccessful && _lastScanError.HasValue)
        {
            var timeSinceLastError = now - _lastScanError.Value;
            var remaining = _errorCooldown - timeSinceLastError;
            if (remaining > TimeSpan.Zero)
                return remaining;
        }

        return TimeSpan.Zero;
    }

    private void ResetCancellationCount()
    {
        if (_consecutiveCancellations > 0)
        {
            _logger.LogInformation("Reseteando contador de cancelaciones: {PreviousCount} -> 0", 
                _consecutiveCancellations);
            _consecutiveCancellations = 0;
            _lastUserCancellation = null;
        }
    }
    #endregion

    #region System State Management
    private async Task HandleSystemSuspend()
    {
        try
        {
            if (_imageProcessor != null)
            {
                await _imageProcessor.CancelScanAsync();
            }
        
            if (_twainService.IsScanning)
            {
                _twainService.StopScan();
                _logger.LogInformation("Escaneo TWAIN detenido por suspensión");
            }
        
            if (_wiaService.IsScanning)
            {
                _wiaService.StopScan();
                _logger.LogInformation("Escaneo WIA detenido por suspensión");
            }

            await _webSocketService.ForceResetScanningState("Sistema entrando en suspensión");
            _tempFileManager.CleanupAll();
            _imageProcessor.ClearPages();
        
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        
            _logger.LogInformation("Limpieza completa realizada para suspensión del sistema");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante limpieza por suspensión del sistema");
        }
    }

    private async Task HandleSystemResume()
    {
        try
        {
            var suspendDuration = _systemStateManager.SuspendDuration;
            _logger.LogInformation("Sistema reanudado después de {Duration}", 
                suspendDuration?.ToString(@"hh\:mm\:ss") ?? "tiempo desconocido");
        
            await Task.Delay(2000);
            await CleanAllState();
            await ReinitializeServices();
            _imageProcessor.ResetCancelFlag();
        
            await _webSocketService.BroadcastMessageAsync(new {
                type = "system_resumed",
                message = "Sistema reanudado - Servicios reinicializados",
                suspendDuration = suspendDuration?.ToString(@"hh\:mm\:ss"),
                timestamp = DateTime.Now
            });
        
            _logger.LogInformation("Servicios reinicializados exitosamente después de reanudación");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reinicializando servicios después de reanudación");
        
            try
            {
                await _systemStateManager.ForceSystemStateCleanup("Error en reanudación");
                await _imageProcessor.CancelScanAsync();
            }
            catch (Exception cleanupEx)
            {
                _logger.LogError(cleanupEx, "Error durante limpieza forzada");
            }
        }
    }

    private async Task HandleSessionLock()
    {
        try
        {
            if (_twainService.IsScanning || _wiaService.IsScanning)
            {
                await _imageProcessor.CancelScanAsync();
                _logger.LogInformation("Estado de escaneo limpiado por bloqueo de sesión");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando bloqueo de sesión");
        }
    }

    private async Task HandleSessionUnlock()
    {
        try
        {
            var systemState = _systemStateManager.GetCurrentState();
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "session_unlocked",
                message = "Sesión desbloqueada - Verificando estado de servicios",
                systemState = systemState.ToString(),
                timestamp = DateTime.Now
            });
            
            _logger.LogInformation("Sesión desbloqueada - Estado del sistema: {State}", systemState);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando desbloqueo de sesión");
        }
    }

    private async Task CleanAllState()
    {
        try
        {
            if (_imageProcessor != null)
            {
                await _imageProcessor.CancelScanAsync();
            }
        
            _imageProcessor.ClearPages();
            _tempFileManager.CleanupAll();
            await _twainService.ForceCleanStateAsync();
        
            if (_wiaService.IsScanning)
            {
                _wiaService.StopScan();
            }
        
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        
            _logger.LogInformation("Estado completamente limpiado");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error limpiando estado");
        }
    }

    private async Task ReinitializeServices()
    {
        try
        {
            const int maxRetries = 3;
            
            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                try
                {
                    await _twainService.ReinitializeAfterResumeAsync();
                    await _wiaService.InitializeAsync();
                    
                    _logger.LogInformation("Servicios de scanner reinicializados exitosamente (intento {Attempt})", attempt);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error reinicializando servicios (intento {Attempt})", attempt);
                    
                    if (attempt < maxRetries)
                    {
                        await Task.Delay(1500 * attempt);  
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico reinicializando servicios de scanner");
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "service_initialization_error",
                message = "Error reinicializando servicios - Reinicia la aplicación",
                error = ex.Message
            });
        }
    }
    #endregion

    #region Device Management
    private async Task<bool> ShowUnifiedDeviceSelectorAndScanAsync()
    {
        try
        {
            if (_systemStateManager.IsSystemSuspended)
            {
                await HandleSystemSuspendedScan();
                return false;
            }

            await PrepareForScan();

            var allDevices = await GetAllAvailableDevicesOptimized();

            if (allDevices.Count == 0)
            {
                await HandleNoDevicesFound();
                return false;
            }

            await NotifyDevicesFound(allDevices);

            var selectedDevice = await ShowUnifiedDeviceSelector(allDevices);

            if (selectedDevice == null)
            {
                await HandleUserCancellation();
                return false;
            }

            ResetCancellationCount();

            var isConnected = await VerifyDeviceConnectivity(selectedDevice);
            if (!isConnected)
            {
                await HandleDeviceNotConnected(selectedDevice);
                return false;
            }

            await NotifyDeviceSelected(selectedDevice);

            bool success = await StartScanWithDevice(selectedDevice);

            if (!success)
            {
                await HandleScanStartError(selectedDevice);
                return false;
            }

            // Escaneo iniciado exitosamente
            _lastScanWasSuccessful = true;
            _lastScanAttempt = DateTime.Now;
            
            return true;
        }
        catch (Exception ex)
        {
            await HandleDeviceSelectorError(ex);
            return false;
        }
    }

    private async Task HandleSystemSuspendedScan()
    {
        _logger.LogWarning("Escaneo bloqueado - Sistema en estado de suspensión");
        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_blocked_system_state",
            message = "Sistema en proceso de suspensión/reanudación - Inténtalo en unos momentos"
        });
        
        await _webSocketService.ForceResetScanningState("Sistema en suspensión");
    }

    private async Task PrepareForScan()
    {
        _imageProcessor.ClearPages();
        _tempFileManager.CleanupAll();

        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_started",
            message = "Iniciando búsqueda rápida de dispositivos...",
            systemState = _systemStateManager.GetCurrentState().ToString()
        });
    }

    private async Task HandleNoDevicesFound()
    {
        _logger.LogWarning("No se encontraron dispositivos de escaneo");
        
        MessageBox.Show(
            "No hay dispositivos de escáner conectados al ordenador.\n\n" +
            "Por favor:\n" +
            "• Conecta el escáner via USB\n" +
            "• Verifica que esté encendido\n" +
            "• Presiona 'Actualizar' después de conectar",
            "No hay dispositivos conectados",
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1,
            MessageBoxOptions.ServiceNotification
        );
        
        await _webSocketService.BroadcastMessageAsync(new {
            type = "no_devices_found",
            message = "No hay dispositivos de escáner conectados al ordenador",
            details = "Verifica que el escáner esté conectado via USB o red y que los drivers estén instalados",
            suggestions = new[] {
                "Conecta el escáner via cable USB",
                "Verifica que el escáner esté encendido", 
                "Instala los drivers del fabricante",
                "Verifica la conexión de red (si es escáner de red)"
            }
        });
        
        await _webSocketService.ForceResetScanningState("No hay dispositivos disponibles");
    }

    private async Task NotifyDevicesFound(List<ScannerDevice> allDevices)
    {
        await _webSocketService.BroadcastMessageAsync(new {
            type = "devices_found",
            count = allDevices.Count,
            devices = allDevices.Select(d => new { d.DisplayName, Type = d.Type.ToString() }),
            message = $"Encontrados {allDevices.Count} dispositivos. Selecciona uno..."
        });
    }

    private async Task HandleUserCancellation()
    {
        _consecutiveCancellations++;
        _lastUserCancellation = DateTime.Now;
        
        _logger.LogInformation("Usuario canceló la selección del dispositivo (Cancelación #{Count})", 
            _consecutiveCancellations);
        
        await _imageProcessor.CancelScanAsync();
        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_cancelled_by_user",
            message = "Escaneo cancelado por el usuario",
            consecutiveCancellations = _consecutiveCancellations,
            maxAllowed = MAX_ALLOWED_CANCELLATIONS
        });
        
        await _webSocketService.ForceResetScanningState("Cancelado por usuario");
    }

    private async Task<bool> VerifyDeviceConnectivity(ScannerDevice selectedDevice)
    {
        await _webSocketService.BroadcastMessageAsync(new {
            type = "device_connectivity_check",
            device = selectedDevice.DisplayName,
            message = $"Verificando conectividad de {selectedDevice.DisplayName}..."
        });

        _logger.LogInformation("Verificando conectividad previa al escaneo: {DeviceName}", 
            selectedDevice.DisplayName);
        
        return await _scannerManager.QuickConnectivityCheckAsync(selectedDevice);
    }

    private async Task HandleDeviceNotConnected(ScannerDevice selectedDevice)
    {
        _logger.LogError("Dispositivo {DeviceName} no está conectado", selectedDevice.DisplayName);
        
        await _webSocketService.BroadcastMessageAsync(new {
            type = "device_not_connected",
            device = selectedDevice.DisplayName,
            message = $"El dispositivo '{selectedDevice.DisplayName}' no está conectado o no responde. Verifica la conexión e inténtalo nuevamente."
        });

        if (_webSocketService is WebSocketService webSocketTyped)
        {
            await webSocketTyped.NotifyDeviceSelectionError($"Dispositivo '{selectedDevice.DisplayName}' no conectado");
        }

        _lastScanWasSuccessful = false;
        _lastScanError = DateTime.Now;
        
        await _webSocketService.ForceResetScanningState("Dispositivo no conectado");
    }

    private async Task NotifyDeviceSelected(ScannerDevice selectedDevice)
    {
        await _webSocketService.BroadcastMessageAsync(new {
            type = "device_selected",
            device = selectedDevice.DisplayName,
            scannerType = selectedDevice.Type.ToString(),
            message = $"{selectedDevice.DisplayName} verificado - iniciando escaneo..."
        });
    }

    private async Task HandleScanStartError(ScannerDevice selectedDevice)
    {
        _logger.LogError("Error iniciando el escaneo con dispositivo: {Device}", selectedDevice.DisplayName);
        
        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_error",
            message = "Error iniciando el escaneo - el dispositivo puede haberse desconectado"
        });

        _lastScanWasSuccessful = false;
        _lastScanError = DateTime.Now;
        
        await _imageProcessor.CancelScanAsync();
    }

    private async Task HandleDeviceSelectorError(Exception ex)
    {
        _logger.LogError(ex, "Error en selector de dispositivos");
        
        await _webSocketService.BroadcastMessageAsync(new {
            type = "error",
            message = $"Error: {ex.Message}"
        });

        _lastScanWasSuccessful = false;
        _lastScanError = DateTime.Now;

        try
        {
            await _imageProcessor.CancelScanAsync();
            await _webSocketService.ForceResetScanningState("Error en selector de dispositivos");
        }
        catch (Exception cancelEx)
        {
            _logger.LogError(cancelEx, "Error cancelando escaneo después de excepción");
        }
    }

    private async Task<List<ScannerDevice>> GetAllAvailableDevicesOptimized()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        _logger.LogInformation("=== OBTENIENDO DISPOSITIVOS RÁPIDAMENTE ===");

        try
        {
            await _webSocketService.BroadcastMessageAsync(new {
                type = "device_search_started",
                message = "Buscando dispositivos conectados..."
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            
            var allDevices = await Task.Run(async () =>
            {
                return await _scannerManager.RefreshDevicesForUI();
            }, cts.Token);
            
            stopwatch.Stop();
            
            _logger.LogInformation("=== DISPOSITIVOS OBTENIDOS EN {ElapsedMs}ms ===", stopwatch.ElapsedMilliseconds);
            
            await NotifyDeviceSearchResult(allDevices, stopwatch.ElapsedMilliseconds);
            
            return allDevices;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning("Búsqueda de dispositivos cancelada por timeout ({ElapsedMs}ms)", stopwatch.ElapsedMilliseconds);
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "device_search_timeout",
                message = "La búsqueda tomó demasiado tiempo - intenta nuevamente",
                elapsedMs = stopwatch.ElapsedMilliseconds
            });
            
            return new List<ScannerDevice>();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Error obteniendo dispositivos en {ElapsedMs}ms", stopwatch.ElapsedMilliseconds);
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "device_search_error",
                message = $"Error buscando dispositivos: {ex.Message}",
                elapsedMs = stopwatch.ElapsedMilliseconds
            });
            
            return new List<ScannerDevice>();
        }
    }

    private async Task NotifyDeviceSearchResult(List<ScannerDevice> allDevices, long elapsedMs)
    {
        if (allDevices.Count > 0)
        {
            foreach (var device in allDevices)
            {
                _logger.LogInformation("   {DisplayName} (Tipo: {Type})", device.DisplayName, device.Type);
            }
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "device_search_success",
                count = allDevices.Count,
                message = $"Encontrados {allDevices.Count} dispositivos",
                elapsedMs = elapsedMs,
                devices = allDevices.Select(d => new { d.DisplayName, Type = d.Type.ToString() })
            });
        }
        else
        {
            _logger.LogWarning("NO SE ENCONTRARON DISPOSITIVOS");
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "device_search_empty", 
                message = "No se encontraron dispositivos. Verifica las conexiones.",
                elapsedMs = elapsedMs
            });
        }
    }

    private async Task<bool> StartScanWithDevice(ScannerDevice selectedDevice)
    {
        try
        {
            _logger.LogInformation("Iniciando escaneo con {Device} (Tipo: {Type})", 
                selectedDevice.DisplayName, selectedDevice.Type);
        
            bool success = await _scannerManager.StartScanAsync(selectedDevice);
        
            if (success)
            {
                if (selectedDevice.Type == ScannerType.WIA)
                {
                    _currentScannerService = _wiaService;
                }
                else
                {
                    _currentScannerService = null;
                }
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando escaneo con dispositivo {Device}", selectedDevice.DisplayName);
            return false;
        }
    }
    
    private async Task<ScannerDevice?> ShowUnifiedDeviceSelector(List<ScannerDevice> devices)
    {
        return await Task.Run(() =>
        {
            ScannerDevice? selectedDevice = null;
            DialogResult result = DialogResult.Cancel;
        
            this.Invoke(new Action(() =>
            {
                try
                {
                    Func<Task<List<ScannerDevice>>> refreshCallback = async () =>
                    {
                        try
                        {
                            _logger.LogInformation("=== REFRESH RÁPIDO DESDE UI ===");
        
                            await _webSocketService.BroadcastMessageAsync(new {
                                type = "device_refresh_started",
                                message = "🔄 Actualizando lista de dispositivos..."
                            });

                            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                            
                            var refreshedDevices = await Task.Run(async () =>
                            {
                                return await _scannerManager.RefreshDevicesForUI();
                            }, cts.Token);
        
                            _logger.LogInformation("Refresh UI completado: {Count} dispositivos", 
                                refreshedDevices.Count);
        
                            return refreshedDevices;
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogWarning("Refresh desde UI cancelado por timeout");
                            return devices;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error en refresh desde UI");
                            return devices;
                        }
                    };

                    using var deviceSelector = new UnifiedDeviceSelectorForm(devices, refreshCallback);
                    result = deviceSelector.ShowDialog(this);
                
                    if (result == DialogResult.OK)
                    {
                        selectedDevice = deviceSelector.SelectedDevice;
                        _logger.LogInformation("Dispositivo seleccionado: {Device}", selectedDevice?.DisplayName ?? "Ninguno");
                    }
                    else
                    {
                        _logger.LogInformation("Selección de dispositivo cancelada por el usuario (DialogResult: {Result})", result);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error mostrando selector de dispositivos");
                    selectedDevice = null;
                }
            }));
        
            return selectedDevice;
        });
    }
    #endregion

    #region Event Handlers - Connectivity
    private async void OnQuickConnectivityCheckStarted(object? sender, string deviceName)
    {
        _logger.LogInformation("Verificación de conectividad iniciada: {DeviceName}", deviceName);
        
        if (_webSocketService is WebSocketService webSocketTyped)
        {
            await webSocketTyped.NotifyConnectivityCheckStarted(deviceName);
        }
    }

    private async void OnQuickConnectivityCheckFailed(object? sender, string message)
    {
        _logger.LogWarning("Verificación de conectividad falló: {Message}", message);
        
        if (_webSocketService is WebSocketService webSocketTyped)
        {
            await webSocketTyped.NotifyConnectivityCheckCompleted("Dispositivo", false, message);
        }
    }

    private async void OnDeviceConnectivityVerified(object? sender, string message)
    {
        _logger.LogInformation("Conectividad verificada: {Message}", message);
        
        if (_webSocketService is WebSocketService webSocketTyped)
        {
            await webSocketTyped.NotifyConnectivityCheckCompleted("Dispositivo", true, message);
        }
    }
    #endregion

    #region Event Handlers - TWAIN
    private async void OnTwainDataTransferred(object? sender, DataTransferredEventArgs e)
    {
        try
        {
            await _imageProcessor.ProcessImageFromFileAsync(e.FileDataPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando imagen TWAIN");
            await _webSocketService.BroadcastMessageAsync(new {
                type = "error",
                message = $"Error procesando imagen: {ex.Message}"
            });
        }
    }

    private async void OnTwainSourceDisabled(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogInformation("ESCANEO TWAIN FINALIZADO");
            
            await ProcessScanCompletion("TWAIN");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizando escaneo TWAIN");
            await _webSocketService.ForceResetScanningState("Error finalizando escaneo TWAIN");
        }
    }

    private void OnTwainTransferReady(object? sender, EventArgs e)
    {
        _logger.LogInformation("Página TWAIN lista para transferir");
        _tempFileManager.CleanupTempFiles();
        
        _ = _webSocketService.BroadcastMessageAsync(new {
            type = "transfer_ready",
            message = "Página lista para transferir"
        });
    }
    #endregion

    #region Event Handlers - WIA
    private async void OnWiaImageScanned(object? sender, string filePath)
    {
        try
        {
            await _imageProcessor.ProcessImageFromFileAsync(filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando imagen WIA");
            await _webSocketService.BroadcastMessageAsync(new {
                type = "error",
                message = $"Error procesando imagen: {ex.Message}"
            });
        }
    }

    private async void OnWiaScanCompleted(object? sender, EventArgs e)
    {
        try
        {
            _logger.LogInformation("ESCANEO WIA FINALIZADO");
            
            await ProcessScanCompletion("WIA");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finalizando escaneo WIA");
            await _webSocketService.ForceResetScanningState("Error finalizando escaneo WIA");
        }
    }

    private async void OnWiaScanError(object? sender, string errorMessage)
    {
        _logger.LogError("Error en escaneo WIA: {Error}", errorMessage);
        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_error",
            message = errorMessage
        });
        
        await _imageProcessor.CancelScanAsync();
    }

    private async Task ProcessScanCompletion(string scannerType)
    {
        if (_imageProcessor.PageCount > 0)
        {
            await _imageProcessor.SendPagesViaWebSocketAsync();
        }
        else
        {
            _logger.LogInformation("No se escanearon páginas");
            await _webSocketService.BroadcastMessageAsync(new {
                type = "scan_completed",
                totalPages = 0,
                message = "Escaneo completado sin páginas"
            });
        }
        
        _imageProcessor.ClearPages();
        _tempFileManager.CleanupAll();
        await _webSocketService.ForceResetScanningState($"Escaneo {scannerType} completado");
        
        _logger.LogInformation("PROCESO COMPLETADO - LISTO PARA NUEVO ESCANEO");
    }
    #endregion

    #region Form Configuration and Events
    private void ConfigureHiddenForm()
    {
        try
        {
            this.StartPosition = FormStartPosition.Manual;
            this.Location = new Point(-32000, -32000);
            this.Size = new Size(300, 200);
            this.MinimumSize = new Size(300, 200);
            this.MaximumSize = new Size(300, 200);
            
            this.WindowState = FormWindowState.Normal;
            this.ShowInTaskbar = true;
            this.Visible = true;
            
            var handle = this.Handle;
            this.SetVisibleCore(true);
            
            _logger.LogInformation("Formulario configurado para TWAIN y WIA");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error configurando formulario");
        }
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(false);
    }

    private void OnFormLoad(object? sender, EventArgs e)
    {
        _logger.LogInformation("Servicio WebSocket iniciado en ws://localhost:9000");
        _logger.LogInformation("Aplicación lista para escanear con TWAIN y WIA");
        _logger.LogInformation("Handle del formulario: {Handle}", this.Handle);
        
        var systemState = _systemStateManager.GetCurrentState();
        _logger.LogInformation("Estado inicial del sistema: {State}", systemState);
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        _logger.LogInformation("Aplicación cerrándose...");
        
        try
        {
            await CleanupResources();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante el cierre");
        }
    }

    private void OnFormClosed(object? sender, FormClosedEventArgs e)
    {
        _logger.LogInformation("Aplicación cerrada");
    }
    #endregion

    #region Cleanup and Disposal
    private async Task CleanupResources()
    {
        try
        {
            _logger.LogInformation("Cerrando servicio...");
            
            if (_twainService?.IsScanning == true)
            {
                _twainService.StopScan();
            }
            
            if (_wiaService?.IsScanning == true)
            {
                _wiaService.StopScan();
            }
            
            await _webSocketService?.ForceResetScanningState("Aplicación cerrándose");
            
            _systemStateManager?.Dispose();
            _twainService?.Dispose();
            _wiaService?.Dispose();
            await _webSocketService?.StopAsync();
            _tempFileManager?.Dispose();
            _imageProcessor?.Dispose();
            _serviceProvider?.Dispose(); 
            
            _logger.LogInformation("Recursos limpiados correctamente");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error limpiando recursos");
        }
    }
    #endregion
}