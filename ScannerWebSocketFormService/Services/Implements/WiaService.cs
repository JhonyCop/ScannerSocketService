using Microsoft.Extensions.Logging;
using ScannerWebSocketFormService.Models;
using ScannerWebSocketFormService.Services.Interface;
using WIA;
using System.Runtime.InteropServices;

namespace ScannerWebSocketFormService.Services.Implements;

public class WiaService : IScannerService, IDisposable  
{
    private readonly ILogger<WiaService> _logger;
    private readonly ITempFileManager _tempFileManager;
    private DeviceManager? _deviceManager;
    private Device? _currentDevice;
    private bool _isScanning = false;
    private bool _disposed = false;

    //  TIMEOUTS OPTIMIZADOS PARA VERIFICACIÓN
    private readonly TimeSpan _scanTimeout = TimeSpan.FromSeconds(30);                    // 30 segundos para escanear (más realista)
    private readonly TimeSpan _deviceResponseTimeout = TimeSpan.FromSeconds(3);          // 3 segundos para respuesta inicial
    
    private readonly TimeSpan _deviceConnectivityTest = TimeSpan.FromMilliseconds(500);  // 500ms para test inicial
    private readonly TimeSpan _quickConnectivityCheck = TimeSpan.FromMilliseconds(800);  // 800ms para conectividad rápida
    private readonly TimeSpan _connectionTimeout = TimeSpan.FromSeconds(2);              // 2 segundos para conexión
    private readonly TimeSpan _disconnectedDeviceTimeout = TimeSpan.FromSeconds(1);      // 1 segundo para dispositivos desconectados

    //  TIMEOUTS ESPECÍFICOS PARA VERIFICACIÓN PRE-ESCANEO
    private readonly TimeSpan _preScanCheck = TimeSpan.FromSeconds(1);                   // 1 segundo para verificar antes de escanear
    private readonly TimeSpan _deviceAvailabilityCheck = TimeSpan.FromSeconds(2);        // 2 segundos para verificar disponibilidad
    
    // Sistema de timeouts y cancelación automática
    private CancellationTokenSource? _scanCancellationSource;
    
    //  CACHE OPTIMIZADO CON TIEMPOS CORREGIDOS
    private readonly Dictionary<string, ScannerDevice> _deviceCache = new();
    private DateTime _lastDeviceScan = DateTime.MinValue;
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(5);                    // 5 minutos para cache de dispositivos
    
    //  Cache de conectividad con tiempos apropiados
    private readonly Dictionary<string, (bool IsConnected, DateTime CheckTime)> _connectivityCache = new();
    private readonly TimeSpan _connectivityCacheExpiry = TimeSpan.FromMinutes(2);        // 2 minutos para conectividad
    
    // Cache de disponibilidad
    private readonly HashSet<string> _knownAvailableDevices = new();
    private readonly HashSet<string> _knownUnresponsiveDevices = new();
    private DateTime _lastAvailabilityCheck = DateTime.MinValue;
    private readonly TimeSpan _availabilityCacheExpiry = TimeSpan.FromMinutes(3);        // 3 minutos para disponibilidad
    

    public event EventHandler<string>? ImageScanned;
    public event EventHandler? ScanCompleted;
    public event EventHandler<string>? ScanError;
    
    // EVENTOS PARA FEEDBACK RÁPIDO
    public event EventHandler<string>? QuickConnectivityCheckStarted;
    public event EventHandler<string>? QuickConnectivityCheckFailed;
    public event EventHandler<string>? DeviceConnectivityVerified;

    public event EventHandler<string>? DeviceTimeout;
    public event EventHandler? AutoRecoveryTriggered;

    public bool IsInitialized => _deviceManager != null;
    public bool IsScanning => _isScanning;
    public ScannerType ServiceType => ScannerType.WIA;

    public WiaService(ILogger<WiaService> logger, ITempFileManager tempFileManager)
    {
        _logger = logger;
        _tempFileManager = tempFileManager;
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (_deviceManager == null)
            {
                _deviceManager = new DeviceManager();
                _logger.LogInformation(" WIA Scanner Service inicializado correctamente");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error inicializando WIA Scanner Service");
            throw;
        }
    }
    
    public async Task<List<ScannerDevice>> GetAvailableDevicesAsync()
{
    var devices = new List<ScannerDevice>();
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    
    try
    {
        _logger.LogInformation("🔍 Iniciando detección WIA optimizada...");
        
        using var globalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        
        await Task.Run(() =>
        {
            DeviceManager? tempDeviceManager = null;
            try
            {
                //  Usar DeviceManager temporal
                tempDeviceManager = new DeviceManager();
                
                foreach (DeviceInfo info in tempDeviceManager.DeviceInfos)
                {
                    if (globalTimeout.Token.IsCancellationRequested)
                        break;
                    
                    if (info.Type != WiaDeviceType.ScannerDeviceType)
                        continue;
                    
                    Device? tempDevice = null;
                    try
                    {
                        // Verificaciones básicas...
                        var deviceId = info.DeviceID;
                        var nameProperty = info.Properties["Name"];
                        var deviceName = nameProperty?.get_Value()?.ToString() ?? "";
                        
                        if (string.IsNullOrEmpty(deviceName))
                            continue;
                        
                        //  NO conectar para solo listar dispositivos
                        devices.Add(new ScannerDevice
                        {
                            Id = deviceId,
                            Name = deviceName,
                            DisplayName = $"WIA - {deviceName}",
                            Type = ScannerType.WIA,
                            NativeDevice = null, //  NO guardar referencia COM
                            IsAvailable = true
                        });
                        
                        _logger.LogInformation("✅ Dispositivo WIA disponible: {DeviceName}", deviceName);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("❌ Dispositivo WIA ignorado: {Error}", ex.Message);
                    }
                    finally
                    {
                        //  Liberar Device si se creó
                        if (tempDevice != null)
                        {
                            ReleaseComObject(tempDevice);
                            tempDevice = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enumerando dispositivos WIA");
            }
            finally
            {
                //  Liberar DeviceManager temporal
                if (tempDeviceManager != null)
                {
                    ReleaseComObject(tempDeviceManager);
                    tempDeviceManager = null;
                }
            }
        }, globalTimeout.Token);
        
        stopwatch.Stop();
        _logger.LogInformation("✅ Detección WIA completada en {ElapsedMs}ms - {Count} dispositivos", 
            stopwatch.ElapsedMilliseconds, devices.Count);
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error crítico en detección WIA");
    }
    
    return devices;
}

    /*public async Task<List<ScannerDevice>> GetAvailableDevicesAsync()
    {
        var devices = new List<ScannerDevice>();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            _logger.LogInformation(" Iniciando detección WIA optimizada...");

            using var globalTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = globalTimeout.Token;

            var deviceManager = new DeviceManager();
            var tasks = new List<Task<ScannerDevice?>>();

            foreach (DeviceInfo info in deviceManager.DeviceInfos)
            {
                if (token.IsCancellationRequested)
                    break;

                tasks.Add(DetectDeviceAsync(info, token));
            }

            var results = await Task.WhenAll(tasks);
            devices = results.Where(d => d != null).Cast<ScannerDevice>().ToList();

            stopwatch.Stop();
            _logger.LogInformation(" Detección WIA completada en {ElapsedMs}ms - {Count} dispositivos encontrados",
                stopwatch.ElapsedMilliseconds, devices.Count);
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(" Detección cancelada por timeout global después de {ElapsedMs}ms",
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, " Error crítico en detección WIA");
        }

        return devices;
    }*/

    private async Task<ScannerDevice?> DetectDeviceAsync(DeviceInfo info, CancellationToken globalToken)
    {
        if (info.Type != WiaDeviceType.ScannerDeviceType)
            return null;

        string deviceId = "";
        string deviceName = "Dispositivo desconocido";

        try
        {
            deviceId = info.DeviceID ?? "";
            if (string.IsNullOrEmpty(deviceId) ||
                deviceId.Contains("ROOT\\LEGACY_") ||
                deviceId.Contains("NULL") ||
                deviceId.Contains("UNKNOWN"))
                return null;

            var nameProperty = info.Properties["Name"];
            deviceName = nameProperty?.get_Value()?.ToString() ?? "";
            if (string.IsNullOrEmpty(deviceName))
                return null;

            _logger.LogDebug(" Verificando dispositivo: {DeviceName} ({DeviceId})", deviceName, deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(" Error accediendo a propiedades: {Error}", ex.Message);
            return null;
        }

        try
        {
            using var deviceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken, deviceTimeout.Token);

            var connectTask = Task.Run(() =>
            {
                try
                {
                    return info.Connect();
                }
                catch
                {
                    return null;
                }
            }, linkedCts.Token);

            var completedTask = await Task.WhenAny(connectTask, Task.Delay(2000, linkedCts.Token));
            if (completedTask != connectTask)
            {
                _logger.LogDebug(" Timeout conectando a: {DeviceName}", deviceName);
                return null;
            }

            var device = await connectTask;
            if (device == null)
            {
                _logger.LogDebug(" Dispositivo no respondió: {DeviceName}", deviceName);
                return null;
            }

            return new ScannerDevice
            {
                Id = deviceId,
                Name = deviceName,
                DisplayName = $"WIA - {deviceName}",
                Type = ScannerType.WIA,
                NativeDevice = device
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(" Error en conexión con {DeviceName}: {Error}", deviceName, ex.Message);
            return null;
        }
    }


        
    //  Verificación rápida de conectividad ANTES del escaneo
    public async Task<bool> QuickConnectivityCheckAsync(ScannerDevice device)
    {
        var deviceId = device.Id ?? "unknown";
        
        // Verificar cache primero
        if (_connectivityCache.TryGetValue(deviceId, out var cached))
        {
            if (DateTime.Now - cached.CheckTime < _connectivityCacheExpiry)
            {
                _logger.LogDebug(" Cache conectividad: {DeviceId} = {IsConnected}", deviceId, cached.IsConnected);
                return cached.IsConnected;
            }
        }

        _logger.LogInformation(" Verificación robusta de conectividad: {DeviceName}", device.Name);
        QuickConnectivityCheckStarted?.Invoke(this, $"Verificando {device.Name}...");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        bool isConnected = false;

        try
        {
            using var cts = new CancellationTokenSource(_deviceConnectivityTest);
            
            isConnected = await Task.Run(() =>
            {
                try
                {
                    if (device.NativeDevice is DeviceInfo deviceInfo)
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        // VERIFICACIÓN 1: ID del dispositivo válido
                        var deviceIdCheck = deviceInfo.DeviceID;
                        if (string.IsNullOrEmpty(deviceIdCheck) || 
                            deviceIdCheck.Contains("ROOT\\LEGACY_") || 
                            deviceIdCheck.Contains("NULL") ||
                            deviceIdCheck.Contains("UNKNOWN"))
                        {
                            _logger.LogDebug(" Device ID inválido: {DeviceId}", deviceIdCheck);
                            return false;
                        }

                        cts.Token.ThrowIfCancellationRequested();

                        // VERIFICACIÓN 2: Propiedades del dispositivo accesibles
                        var nameProperty = deviceInfo.Properties["Name"];
                        var name = nameProperty?.get_Value()?.ToString();
                        
                        if (string.IsNullOrEmpty(name))
                        {
                            _logger.LogDebug(" Nombre del dispositivo no accesible");
                            return false;
                        }

                        cts.Token.ThrowIfCancellationRequested();

                        // VERIFICACIÓN 3: Intentar acceder a propiedades adicionales
                        try
                        {
                            var statusProperty = deviceInfo.Properties["Connection Status"];
                            var status = statusProperty?.get_Value();
                            _logger.LogDebug(" Estado de conexión: {Status}", status);
                        }
                        catch
                        {
                            _logger.LogDebug(" No se pudo verificar estado de conexión");
                        }

                        // VERIFICACIÓN 4: Intentar conectar brevemente (test real) - CORREGIDO
                        cts.Token.ThrowIfCancellationRequested();
                        
                        try
                        {
                            //  FIX: Usar Task con timeout sincrónico
                            var quickTestTask = Task.Run(() =>
                            {
                                //  FIX: Usar el token padre en lugar de crear uno nuevo
                                if (cts.Token.IsCancellationRequested)
                                    return false;

                                try
                                {
                                    var testDevice = deviceInfo.Connect();
                                    var itemsCount = testDevice?.Items?.Count;
                                    _logger.LogDebug(" Test de conexión real exitoso");
                                    return true;
                                }
                                catch (COMException comEx)
                                {
                                    _logger.LogDebug(" Test de conexión real falló: COM Error 0x{ErrorCode:X8}", comEx.ErrorCode);
                                    return false;
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogDebug(" Test de conexión real falló: {Message}", ex.Message);
                                    return false;
                                }
                            });

                            //  FIX: Usar Wait con timeout en lugar de await WaitAsync
                            if (quickTestTask.Wait(10000)) // 300ms timeout
                            {
                                return quickTestTask.Result;
                            }
                            else
                            {
                                _logger.LogDebug(" Test de conexión real falló por timeout");
                                return false;
                            }
                        }
                        catch (AggregateException aggEx)
                        {
                            var innerEx = aggEx.GetBaseException();
                            if (innerEx is COMException comEx)
                            {
                                _logger.LogDebug(" Test de conexión real falló: COM Error 0x{ErrorCode:X8}", comEx.ErrorCode);
                            }
                            else
                            {
                                _logger.LogDebug(" Test de conexión real falló: {Message}", innerEx.Message);
                            }
                            return false;
                        }
                        catch (OperationCanceledException)
                        {
                            _logger.LogDebug(" Test de conexión real cancelado");
                            return false;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(" Error inesperado en test de conexión: {Message}", ex.Message);
                            return false;
                        }
                    }

                    return false;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug(" Verificación cancelada por timeout");
                    return false;
                }
                catch (COMException comEx)
                {
                    _logger.LogDebug(" COM Error en verificación: 0x{ErrorCode:X8}", comEx.ErrorCode);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(" Error en verificación: {Message}", ex.Message);
                    return false;
                }
            }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(" Timeout en verificación robusta: {DeviceName} ({ElapsedMs}ms)", 
                device.Name, stopwatch.ElapsedMilliseconds);
            isConnected = false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, " Error en verificación robusta: {DeviceName}", device.Name);
            isConnected = false;
        }
        finally
        {
            stopwatch.Stop();
        }

        // Guardar en cache con tiempo de vida más corto para dispositivos desconectados
        var cacheExpiry = isConnected ? _connectivityCacheExpiry : TimeSpan.FromMinutes(1);
        _connectivityCache[deviceId] = (isConnected, DateTime.Now);

        _logger.LogInformation(" Conectividad {DeviceName}: {Result} ({ElapsedMs}ms)", 
            device.Name, isConnected ? " CONECTADO" : " DESCONECTADO", stopwatch.ElapsedMilliseconds);

        if (isConnected)
        {
            DeviceConnectivityVerified?.Invoke(this, $"{device.Name} está conectado y listo para escanear");
        }
        else
        {
            QuickConnectivityCheckFailed?.Invoke(this, $"{device.Name} está desconectado o no responde");
        }

        return isConnected;
    }

    private async Task<List<ScannerDevice>> GetDevicesWithTimeoutAndRecovery()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        try
        {
            _logger.LogInformation(" Iniciando detección WIA rápida...");
            
            using var cts = new CancellationTokenSource(_deviceResponseTimeout);
            
            var devices = await Task.Run(async () =>
            {
                return await GetDevicesOptimizedWithValidation(cts.Token);
            }, cts.Token);
            
            stopwatch.Stop();
            _logger.LogInformation(" Detección WIA completada en {ElapsedMs}ms - {Count} dispositivos", 
                stopwatch.ElapsedMilliseconds, devices.Count);
            
            return devices;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            _logger.LogWarning(" Timeout en detección WIA después de {ElapsedMs}ms", 
                stopwatch.ElapsedMilliseconds);
            
            await ForceReinitializeAfterTimeout();
            return await GetBasicDeviceListAsFallback();
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, " Error crítico en detección WIA");
            
            await ForceReinitializeAfterTimeout();
            return await GetBasicDeviceListAsFallback();
        }
    }
    
    private async Task<List<ScannerDevice>> GetDevicesOptimizedWithValidation(CancellationToken cancellationToken)
    {
        // Usar cache si está disponible y no ha expirado
        if (DateTime.Now - _lastDeviceScan < _cacheExpiry && _deviceCache.Count > 0)
        {
            _logger.LogInformation(" Cache WIA válido - {Count} dispositivos", _deviceCache.Count);
            return _deviceCache.Values.Where(d => !IsDeviceKnownUnresponsive(d.Id)).ToList();
        }

        var devices = new List<ScannerDevice>();
        
        if (_deviceManager == null)
        {
            await InitializeAsync();
        }

        try
        {
            _deviceCache.Clear();

            foreach (DeviceInfo deviceInfo in _deviceManager!.DeviceInfos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                try
                {
                    if (deviceInfo.Type != WiaDeviceType.ScannerDeviceType)
                        continue;

                    var deviceId = GetDeviceIdFast(deviceInfo);
                    
                    if (IsDeviceKnownUnresponsive(deviceId))
                    {
                        _logger.LogDebug(" Omitiendo dispositivo no responsivo: {DeviceId}", deviceId);
                        continue;
                    }

                    // Verificación SÚPER rápida con timeout mínimo
                    if (!await IsDeviceResponsiveSuperFastAsync(deviceInfo, cancellationToken))
                    {
                        _knownUnresponsiveDevices.Add(deviceId);
                        _logger.LogDebug(" Dispositivo no responde rápidamente: {DeviceId}", deviceId);
                        continue;
                    }

                    var deviceName = GetDeviceNameFast(deviceInfo);

                    var scannerDevice = new ScannerDevice
                    {
                        Id = deviceId,
                        Name = deviceName,
                        DisplayName = $"WIA-{deviceName}",
                        Type = ScannerType.WIA,
                        NativeDevice = deviceInfo
                    };

                    devices.Add(scannerDevice);
                    _deviceCache[deviceId] = scannerDevice;
                    _knownAvailableDevices.Add(deviceId);

                    _logger.LogDebug(" WIA device responsivo: {Name} ({Id})", deviceName, deviceId);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (COMException comEx)
                {
                    var deviceId = GetDeviceIdFast(deviceInfo);
                    _knownUnresponsiveDevices.Add(deviceId);
                    _logger.LogDebug(" COM Error en dispositivo {DeviceId}: 0x{ErrorCode:X8}", deviceId, comEx.ErrorCode);
                    continue;
                }
                catch (Exception ex)
                {
                    var deviceId = GetDeviceIdFast(deviceInfo);
                    _knownUnresponsiveDevices.Add(deviceId);
                    _logger.LogDebug(" Error en dispositivo {DeviceId}: {Error}", deviceId, ex.Message);
                    continue;
                }
            }

            _lastDeviceScan = DateTime.Now;
            _lastAvailabilityCheck = DateTime.Now;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(" Detección WIA cancelada por timeout");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error en detección WIA optimizada");
            throw;
        }

        return devices;
    }
    
    //  Verificación SÚPER rápida (usando el timeout correcto)
    private async Task<bool> IsDeviceResponsiveSuperFastAsync(DeviceInfo deviceInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_quickConnectivityCheck); // Usar el timeout definido

            return await Task.Run<bool>(() =>
            {
                try
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();

                    var deviceId = deviceInfo.DeviceID;
                    if (string.IsNullOrEmpty(deviceId) ||
                        deviceId.Contains("ROOT\\LEGACY_") ||
                        deviceId.Contains("HTREE\\ROOT\\") ||
                        deviceId.Contains("NULL"))
                    {
                        return false;
                    }

                    timeoutCts.Token.ThrowIfCancellationRequested();

                    var nameProperty = deviceInfo.Properties["Name"];
                    return nameProperty?.get_Value() != null;
                }
                catch
                {
                    return false;
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> IsDeviceResponsiveAsync(DeviceInfo deviceInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_deviceAvailabilityCheck); // Usar el timeout definido
        
            return await Task.Run(() =>
            {
                try
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();
                
                    var deviceId = deviceInfo.DeviceID;
                    if (string.IsNullOrEmpty(deviceId))
                        return false;

                    if (deviceId.Contains("ROOT\\LEGACY_") || 
                        deviceId.Contains("HTREE\\ROOT\\") || 
                        deviceId.Contains("NULL"))
                    {
                        return false;
                    }

                    timeoutCts.Token.ThrowIfCancellationRequested();
                    var nameProperty = deviceInfo.Properties["Name"];
                
                    return true;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
                catch
                {
                    return false;
                }
            });
        }
        catch
        {
            return false;
        }
    }
    
    private bool IsDeviceKnownUnresponsive(string deviceId)
    {
        return _knownUnresponsiveDevices.Contains(deviceId);
    }
    
    private async Task ForceReinitializeAfterTimeout()
    {
        try
        {
            _logger.LogInformation(" Ejecutando reinicialización rápida...");
            
            _deviceCache.Clear();
            _connectivityCache.Clear();
            _knownAvailableDevices.Clear();
            _knownUnresponsiveDevices.Clear();
            _lastDeviceScan = DateTime.MinValue;
            _lastAvailabilityCheck = DateTime.MinValue;
            
            _deviceManager = null;
            await Task.Delay(1000); // 1 segundo para estabilización
            
            _deviceManager = new DeviceManager();
            await Task.Delay(500); // 500ms adicionales
            
            AutoRecoveryTriggered?.Invoke(this, EventArgs.Empty);
            _logger.LogInformation(" Reinicialización rápida completada");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error en reinicialización rápida");
        }
    }
    
    private async Task<List<ScannerDevice>> GetBasicDeviceListAsFallback()
    {
        var devices = new List<ScannerDevice>();
        
        try
        {
            _logger.LogInformation(" Usando detección básica como fallback...");
            
            if (_deviceManager?.DeviceInfos != null)
            {
                int count = 0;
                foreach (DeviceInfo deviceInfo in _deviceManager.DeviceInfos)
                {
                    try
                    {
                        if (deviceInfo.Type == WiaDeviceType.ScannerDeviceType)
                        {
                            count++;
                            
                            var fallbackDevice = new ScannerDevice
                            {
                                Id = $"wia_fallback_{count}",
                                Name = $"WIA Scanner {count}",
                                DisplayName = $"WIA-Scanner {count} [Detección básica]",
                                Type = ScannerType.WIA,
                                NativeDevice = deviceInfo
                            };
                            
                            devices.Add(fallbackDevice);
                            
                            if (count >= 3) break;
                        }
                    }
                    catch
                    {
                        // Ignorar errores en fallback
                    }
                }
            }
            
            _logger.LogInformation(" Fallback completado: {Count} dispositivos básicos", devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, " Error incluso en fallback básico");
        }

        return devices;
    }

    private string GetDeviceIdFast(DeviceInfo deviceInfo)
    {
        try
        {
            return deviceInfo.DeviceID ?? $"wia_fast_{Guid.NewGuid():N}";
        }
        catch
        {
            return $"wia_fast_{Guid.NewGuid():N}";
        }
    }

    private string GetDeviceNameFast(DeviceInfo deviceInfo)
    {
        try
        {
            var nameProperty = deviceInfo.Properties["Name"];
            if (nameProperty?.get_Value() != null)
            {
                var name = nameProperty.get_Value().ToString();
                if (!string.IsNullOrEmpty(name))
                    return name;
            }
            
            var deviceId = deviceInfo.DeviceID;
            if (!string.IsNullOrEmpty(deviceId))
            {
                var parts = deviceId.Split('\\', '&');
                foreach (var part in parts)
                {
                    if (part.Length > 3 && part.Length < 30 && !part.All(char.IsDigit) && !part.Contains("VID") && !part.Contains("PID"))
                        return part;
                }
            }

            return "WIA Scanner";
        }
        catch
        {
            return "WIA Scanner";
        }
    }

    //  OPTIMIZADO: StartScan con verificación previa rápida
    public async Task<bool> StartScanAsync(ScannerDevice device, bool showUI = false)
    {
        if (_isScanning)
        {
            _logger.LogWarning(" Ya hay un escaneo WIA en progreso");
            ScanError?.Invoke(this, " Ya hay un escaneo en progreso. Espera a que termine.");
            return false;
        }

        if (device.Type != ScannerType.WIA)
        {
            _logger.LogError(" Dispositivo no válido para WIA");
            ScanError?.Invoke(this, " Este dispositivo no es compatible con WIA");
            return false;
        }

        try
        {
            // Verificación previa de conexión
            _logger.LogInformation(" Verificando disponibilidad del dispositivo: {DeviceName}", device.Name);

            var isConnected = await QuickConnectivityCheckAsync(device);
            if (!isConnected)
            {
                _logger.LogError(" Dispositivo {DeviceName} no está conectado o no responde", device.Name);
                ScanError?.Invoke(this, $" {device.Name} está desconectado.\n\n🔌 Por favor:\n• Verifica que esté encendido y conectado\n• Selecciona otro dispositivo de la lista");
                return false;
            }

            // Segunda verificación: acceso a propiedades
            _logger.LogInformation(" Verificando acceso al dispositivo...");

            if (device.NativeDevice is DeviceInfo deviceInfo)
            {
                try
                {
                    using var quickAccessTest = new CancellationTokenSource(_preScanCheck);
                    
                    var hasAccess = await Task.Run<bool>(() =>
                    {
                        try
                        {
                            if (quickAccessTest.Token.IsCancellationRequested)
                                return false;

                            var nameTest = deviceInfo.Properties["Name"]?.get_Value();
                            var typeTest = deviceInfo.Type;
                            return nameTest != null;
                        }
                        catch
                        {
                            return false;
                        }
                    }, quickAccessTest.Token);

                    if (!hasAccess)
                    {
                        throw new OperationCanceledException();
                    }
                }
                catch (OperationCanceledException)
                {
                    _logger.LogError(" Timeout verificando acceso al dispositivo {DeviceName}", device.Name);
                    ScanError?.Invoke(this, $" {device.Name} no responde.\n\n El dispositivo no responde a tiempo.\nPor favor selecciona otro dispositivo.");
                    return false;
                }
            }

            _isScanning = true;

            _scanCancellationSource = new CancellationTokenSource(_scanTimeout);
            var cancellationToken = _scanCancellationSource.Token;

            _logger.LogInformation(" Iniciando escaneo WIA: {DeviceName}", device.Name);

            if (!await ConnectToDeviceWithTimeoutAsync(device, cancellationToken))
            {
                return false;
            }

            // Determina el método de escaneo según showUI
            Task scanTask = showUI
                ? ScanWithUIWithTimeout(cancellationToken)
                : ScanWithoutUIWithTimeout(cancellationToken);

            // Monitor de timeout
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(_scanTimeout);
                    if (_isScanning && !cancellationToken.IsCancellationRequested)
                    {
                        _logger.LogWarning(" TIMEOUT: Escaneo WIA tomó más de {Timeout}s", _scanTimeout.TotalSeconds);
                        await AutoCancelScanWithRecovery($" {device.Name} no responde.\n\n El escaneo tomó demasiado tiempo.\nPor favor selecciona otro dispositivo.");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, " Error en el monitor de timeout");
                }
            });

            await scanTask;
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(" Escaneo WIA cancelado por timeout");
            await AutoCancelScanWithRecovery($" {device.Name} no responde.\n\n Timeout automático del dispositivo.\nPor favor selecciona otro dispositivo.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error iniciando escaneo WIA");
            await AutoCancelScanWithRecovery($" Error escaneando con {device.Name}.\n\n {ex.Message}\n\nPor favor selecciona otro dispositivo.");
            return false;
        }
        finally
        {
            _isScanning = false;
        }
    }

    private async Task<bool> ConnectToDeviceWithTimeoutAsync(ScannerDevice device, CancellationToken cancellationToken)
{
    Device? newDevice = null;
    DeviceManager? tempDeviceManager = null;
    
    try
    {
        //  Liberar dispositivo anterior
        if (_currentDevice != null)
        {
            ReleaseComObject(_currentDevice);
            _currentDevice = null;
        }

        _logger.LogInformation("🔌 Conectando a dispositivo WIA: {DeviceName}", device.Name);
        
        //  Crear DeviceManager temporal para conectar
        tempDeviceManager = new DeviceManager();
        
        // Buscar el dispositivo por ID
        foreach (DeviceInfo info in tempDeviceManager.DeviceInfos)
        {
            if (info.DeviceID == device.Id)
            {
                var connectionTask = Task.Run(() =>
                {
                    try
                    {
                        return info.Connect();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error en Connect()");
                        return null;
                    }
                }, cancellationToken);

                newDevice = await connectionTask.WaitAsync(_connectionTimeout, cancellationToken);
                break;
            }
        }
        
        if (newDevice != null)
        {
            _currentDevice = newDevice;
            _logger.LogInformation("✅ Conexión WIA exitosa: {DeviceName}", device.Name);
            return true;
        }
        else
        {
            _logger.LogError("❌ No se pudo conectar a: {DeviceName}", device.Name);
            return false;
        }
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error conectando a dispositivo WIA");
        
        //  Liberar en caso de error
        if (newDevice != null && newDevice != _currentDevice)
        {
            ReleaseComObject(newDevice);
        }
        
        return false;
    }
    finally
    {
        //  Siempre liberar DeviceManager temporal
        if (tempDeviceManager != null)
        {
            ReleaseComObject(tempDeviceManager);
        }
    }
}
    
    private async Task ScanWithoutUIWithTimeout(CancellationToken cancellationToken)
    {
        try
        {
            if (_currentDevice == null || _currentDevice.Items == null || _currentDevice.Items.Count == 0)
            {
                throw new COMException("El escáner no está listo o no tiene elementos", unchecked((int)0x80210003));
            }

            _logger.LogInformation(" Iniciando escaneo WIA sin UI");

            var item = _currentDevice.Items[1];
            ConfigureScanSettingsOptimized(item);

            var format = WIA.FormatID.wiaFormatJPEG;

            var transferTask = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return (ImageFile)item.Transfer(format); //  Aquí fallará si no hay hoja
            }, cancellationToken);

            var imageFile = await transferTask.WaitAsync(_scanTimeout, cancellationToken);

            await ProcessScannedImageOptimized(imageFile); // Aquí guardas o envías la imagen

            _logger.LogInformation(" Escaneo WIA completado exitosamente");
            ScanCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (COMException ex) when ((uint)ex.ErrorCode == 0x80210003)
        {
            _logger.LogWarning(" El escáner no está listo para transferir (0x80210003)");
            ScanError?.Invoke(this,
                $" El escáner no está listo.\n\n🔍 Asegúrate de que hay una hoja en la bandeja.\n🔌 Verifica que esté encendido y conectado.\n📄 Si el escáner tiene ADF, coloca una hoja antes de escanear.");
            throw;
        }
        catch (TimeoutException)
        {
            _logger.LogError(" Timeout en escaneo WIA");
            throw new OperationCanceledException("Timeout en escaneo");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error en escaneo WIA sin UI");
            throw;
        }
        finally
        {
            _isScanning = false;
        }
    }


    
    private async Task ScanWithUIWithTimeout(CancellationToken cancellationToken)
    {
        try
        {
            if (_currentDevice == null) return;

            _logger.LogInformation(" Iniciando escaneo WIA con UI");

            var dialogTask = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                var wiaCommonDialog = new WIA.CommonDialog();
                return (ImageFile)wiaCommonDialog.ShowAcquireImage(
                    WiaDeviceType.ScannerDeviceType,
                    WiaImageIntent.ColorIntent,
                    WiaImageBias.MaximizeQuality,
                    "{00000000-0000-0000-0000-000000000000}",
                    true,
                    true,
                    false
                );
            }, cancellationToken);

            var imageFile = await dialogTask.WaitAsync(_scanTimeout, cancellationToken);

            if (imageFile != null)
            {
                await ProcessScannedImageOptimized(imageFile);
                _logger.LogInformation(" Escaneo WIA con UI completado");
                ScanCompleted?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                _logger.LogInformation(" Escaneo WIA cancelado por usuario");
                ScanCompleted?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (TimeoutException)
        {
            _logger.LogError(" Timeout en escaneo WIA con UI");
            throw new OperationCanceledException("Timeout en escaneo con UI");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(" Escaneo WIA con UI cancelado");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error en escaneo WIA con UI");
            throw;
        }
        finally
        {
            _isScanning = false;
        }
    }
    
    private async Task AutoCancelScanWithRecovery(string reason)
    {
        try
        {
            _logger.LogWarning(" AUTO-CANCELACIÓN: {Reason}", reason);
            
            _scanCancellationSource?.Cancel();
            _isScanning = false;
            _currentDevice = null;
            
            if (_currentDevice != null)
            {
                var deviceId = GetDeviceIdFast((DeviceInfo)((ScannerDevice)_currentDevice).NativeDevice);
                _knownUnresponsiveDevices.Add(deviceId);
            }
            
            ScanError?.Invoke(this, $" {reason}");
            DeviceTimeout?.Invoke(this, reason);
            AutoRecoveryTriggered?.Invoke(this, EventArgs.Empty);
            
            _logger.LogInformation(" Auto-recovery completado");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error en auto-cancelación");
        }
    }

    private void ConfigureScanSettingsOptimized(Item item)
    {
        try
        {
            SetPropertySafe(item, "6147", 200); // XRES
            SetPropertySafe(item, "6148", 200); // YRES
            SetPropertySafe(item, "6146", 1);   // Color mode
            SetPropertySafe(item, "6149", 24);  // Bits per pixel

            _logger.LogDebug(" WIA settings: 200 DPI, Color RGB");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, " Some WIA properties could not be set");
        }
    }

    private void SetPropertySafe(Item item, string propertyId, object value)
    {
        try
        {
            item.Properties[propertyId].set_Value(ref value);
        }
        catch
        {
            // Ignorar silenciosamente errores de propiedades
        }
    }

    private async Task ProcessScannedImageOptimized(ImageFile imageFile)
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var fileName = $"wia_scan_{timestamp}.jpg";
            var filePath = Path.Combine(_tempFileManager.TempFolder, fileName);

            // Guardar archivo
            imageFile.SaveFile(filePath);
            _tempFileManager.AddTempFile(filePath);
        
            //  Liberar ImageFile inmediatamente
            ReleaseComObject(imageFile);

            _logger.LogInformation("✅ WIA image saved: {FileName}", fileName);
            ImageScanned?.Invoke(this, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing WIA image");
            throw;
        }
    }

    public void StopScan()
    {
        _logger.LogInformation("🛑 Deteniendo escaneo WIA...");
    
        _scanCancellationSource?.Cancel();
        _isScanning = false;
    
        //  Liberar dispositivo COM completamente
        if (_currentDevice != null)
        {
            try
            {
                ReleaseComObject(_currentDevice);
                _logger.LogDebug("✅ Dispositivo COM liberado");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error liberando dispositivo COM");
            }
            finally
            {
                _currentDevice = null;
            }
        }
    
        //  Liberar DeviceManager si existe
        if (_deviceManager != null)
        {
            try
            {
                ReleaseComObject(_deviceManager);
                _logger.LogDebug("✅ DeviceManager liberado");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error liberando DeviceManager");
            }
            finally
            {
                _deviceManager = null;
            }
        }
    
        //  Forzar recolección después de liberar COM
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    
        _logger.LogInformation("✅ Escaneo WIA detenido y memoria liberada");
    }
    public void ClearDeviceCache()
    {
        _deviceCache.Clear();
        _knownAvailableDevices.Clear();
        _knownUnresponsiveDevices.Clear();
        _connectivityCache.Clear();
        _lastDeviceScan = DateTime.MinValue;
        _lastAvailabilityCheck = DateTime.MinValue;
        _logger.LogInformation(" Cache WIA completo limpiado");
    }
    
    private void ReleaseComObject(object obj)
    {
        if (obj != null && Marshal.IsComObject(obj))
        {
            try
            {
                Marshal.ReleaseComObject(obj);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error liberando objeto COM");
            }
        }
    }


    public void Dispose()
    {
        if (!_disposed)
        {
            StopScan();
        
            // Limpiar caches
            _deviceCache.Clear();
            _knownAvailableDevices.Clear();
            _knownUnresponsiveDevices.Clear();
            _connectivityCache.Clear();
        
            // Liberar timer
            _scanCancellationSource?.Dispose();
        
            //  Liberar cualquier objeto COM restante
            if (_currentDevice != null)
            {
                ReleaseComObject(_currentDevice);
                _currentDevice = null;
            }
        
            if (_deviceManager != null)
            {
                ReleaseComObject(_deviceManager);
                _deviceManager = null;
            }
        
            _disposed = true;
        
            // Forzar limpieza final
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        
            _logger.LogInformation("✅ WIA Scanner Service disposed");
        }
    }
}