using Microsoft.Extensions.Logging;
using ScannerWebSocketFormService.Models;
using ScannerWebSocketFormService.Services.Interface;

namespace ScannerWebSocketFormService.Services.Implements;

public class ScannerManager : IScannerManager
{
    private readonly ITwainService _twainService;
    private readonly IScannerService _wiaService;
    private readonly WiaService _wiaServiceTyped;
    private readonly ILogger<ScannerManager> _logger;

    // Cache de conectividad para evitar verificaciones repetitivas
    private readonly Dictionary<string, (bool IsConnected, DateTime CheckTime)> _connectivityCache = new();
    private readonly TimeSpan _connectivityCacheExpiry = TimeSpan.FromMinutes(2);

    public bool IsScanning => _twainService?.IsScanning == true || _wiaService?.IsScanning == true;

    public ScannerManager(
        ITwainService twainService, 
        IScannerService wiaService, 
        ILogger<ScannerManager> logger)
    {
        _twainService = twainService;
        _wiaService = wiaService;
        _wiaServiceTyped = wiaService as WiaService ?? throw new ArgumentException("WiaService no es del tipo esperado");
        _logger = logger;
    }

    public async Task<List<ScannerDevice>> GetAllUpdatedDevicesAsync(bool forceRefresh = true)
    {
        _logger.LogInformation("GetAllUpdatedDevicesAsync - forceRefresh: {ForceRefresh}", forceRefresh);
    
        if (forceRefresh)
        {
            return await RefreshDevicesForUI();
        }

        try
        {
            var wiaDevices = await _wiaService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
            var twainDevices = await _twainService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
        
            var allDevices = new List<ScannerDevice>();
            allDevices.AddRange(wiaDevices);
            allDevices.AddRange(twainDevices);
        
            return RemoveDuplicateDevicesOptimized(allDevices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en obtención rápida de dispositivos");
            return new List<ScannerDevice>();
        }
    }

    // Verificación rápida de conectividad para un dispositivo específico
    public async Task<bool> QuickConnectivityCheckAsync(ScannerDevice device)
    {
        if (device == null) return false;

        var deviceKey = $"{device.Type}_{device.Id ?? device.Name}";
        
        // Verificar cache primero
        if (_connectivityCache.TryGetValue(deviceKey, out var cached))
        {
            if (DateTime.Now - cached.CheckTime < _connectivityCacheExpiry)
            {
                _logger.LogDebug("Cache conectividad para {DeviceName}: {IsConnected}", device.Name, cached.IsConnected);
                return cached.IsConnected;
            }
        }

        _logger.LogInformation("Verificación rápida de conectividad: {DeviceName} ({Type})", device.Name, device.Type);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        bool isConnected = false;

        try
        {
            // Usar verificación específica según el tipo
            if (device.Type == ScannerType.WIA && _wiaServiceTyped != null)
            {
                isConnected = await _wiaServiceTyped.QuickConnectivityCheckAsync(device);
            }
            else if (device.Type == ScannerType.TWAIN)
            {
                isConnected = await QuickTwainConnectivityCheck(device);
            }

            // Guardar en cache
            _connectivityCache[deviceKey] = (isConnected, DateTime.Now);

            stopwatch.Stop();
            _logger.LogInformation("Conectividad {DeviceName}: {Result} ({ElapsedMs}ms)", 
                device.Name, 
                isConnected ? "CONECTADO" : "DESCONECTADO", 
                stopwatch.ElapsedMilliseconds);

            return isConnected;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Error verificando conectividad {DeviceName} ({ElapsedMs}ms)", 
                device.Name, stopwatch.ElapsedMilliseconds);
            
            // En caso de error, guardar como no conectado en cache
            _connectivityCache[deviceKey] = (false, DateTime.Now);
            return false;
        }
    }

    // Verificación rápida para TWAIN (más conservadora)
    private async Task<bool> QuickTwainConnectivityCheck(ScannerDevice device)
    {
        try
        {
            // Para TWAIN, solo verificar que el servicio esté inicializado y el dispositivo exista
            if (!_twainService.IsInitialized)
            {
                _logger.LogDebug("TWAIN no inicializado - dispositivo considerado no conectado");
                return false;
            }

            // Verificación básica: si el dispositivo está en la lista reciente, probablemente esté conectado
            var recentDevices = await _twainService.GetAvailableDevicesAsync();
            var deviceExists = recentDevices?.Any(d => 
                string.Equals(d.Name, device.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Id, device.Id, StringComparison.OrdinalIgnoreCase)) ?? false;

            return deviceExists;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error en verificación TWAIN para {DeviceName}", device.Name);
            return false;
        }
    }

    public async Task<bool> StartScanAsync(ScannerDevice device)
    {
        try
        {
            _logger.LogInformation("Iniciando escaneo con verificación previa: {DeviceName} (Tipo: {Type})", 
                device.Name, device.Type);

            // Verificación rápida de conectividad ANTES del escaneo
            _logger.LogInformation("Verificando conectividad previa al escaneo...");
            var isConnected = await QuickConnectivityCheckAsync(device);

            if (!isConnected)
            {
                _logger.LogWarning("Dispositivo {DeviceName} no está conectado - abortando escaneo", device.Name);
                return false;
            }

            _logger.LogInformation("Dispositivo {DeviceName} verificado como conectado - procediendo con escaneo", device.Name);

            // Proceder con el escaneo según el tipo
            return device.Type switch
            {
                ScannerType.TWAIN => await _twainService.StartScanAsync(device),
                ScannerType.WIA => await _wiaService.StartScanAsync(device),
                _ => throw new NotSupportedException($"Tipo de scanner no soportado: {device.Type}")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando escaneo con dispositivo {DeviceName}", device.Name);
            return false;
        }
    }

    public async Task<List<ScannerDevice>> RefreshDevicesForUI()
    {
        _logger.LogInformation("=== REFRESH DE DISPOSITIVOS PARA UI (OPTIMIZADO) ===");
    
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Obtener dispositivos de ambos servicios con timeouts
            var deviceTasks = new List<Task<List<ScannerDevice>>>();

            // WIA con timeout
            deviceTasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                    return await _wiaService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error obteniendo dispositivos WIA");
                    return new List<ScannerDevice>();
                }
            }));

            // TWAIN con timeout
            deviceTasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                    return await _twainService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error obteniendo dispositivos TWAIN");
                    return new List<ScannerDevice>();
                }
            }));

            // Esperar resultados
            var results = await Task.WhenAll(deviceTasks);
            var wiaDevices = results[0];
            var twainDevices = results[1];
        
            _logger.LogInformation("WIA encontrados: {Count}", wiaDevices.Count);
            _logger.LogInformation("TWAIN encontrados: {Count}", twainDevices.Count);
        
            // Combinar dispositivos
            var allDevices = new List<ScannerDevice>();
            allDevices.AddRange(wiaDevices);
            allDevices.AddRange(twainDevices);
        
            // Eliminar duplicados
            var uniqueDevices = RemoveDuplicateDevicesOptimized(allDevices);

            stopwatch.Stop();
            _logger.LogInformation("=== REFRESH COMPLETADO EN {ElapsedMs}ms: {Count} dispositivos ===", 
                stopwatch.ElapsedMilliseconds, uniqueDevices.Count);
        
            foreach (var device in uniqueDevices)
            {
                _logger.LogInformation("📱 [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
        
            return uniqueDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en refresh optimizado de dispositivos");
            return new List<ScannerDevice>();
        }
    }

    public async Task<List<ScannerDevice>> ForceRefreshAllDevices()
    {
        _logger.LogInformation("=== REFRESH FORZADO OPTIMIZADO DE TODOS LOS DISPOSITIVOS ===");
        
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Limpiar cachés
            _logger.LogDebug("Limpiando cachés...");
            _connectivityCache.Clear();
            
            if (_wiaService is WiaService wiaImpl)
            {
                wiaImpl.ClearDeviceCache();
                _logger.LogDebug("Caché WIA limpiado");
            }

            // Liberación de recursos
            _logger.LogDebug("Liberando recursos...");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(400);

            // Reinicializar servicios
            _logger.LogDebug("Reinicializando servicios...");
            
            var reinitTasks = new List<Task>
            {
                // Reinicializar WIA
                Task.Run(async () =>
                {
                    try
                    {
                        await _wiaService.InitializeAsync();
                        await Task.Delay(600);
                        _logger.LogDebug("WIA reinicializado");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reinicializando WIA");
                    }
                }),

                // TWAIN más conservador
                Task.Run(async () =>
                {
                    try
                    {
                        if (!_twainService.IsInitialized)
                        {
                            _logger.LogDebug("TWAIN no inicializado - inicializando...");
                            await _twainService.InitializeAsync();
                            await Task.Delay(1000);
                            _logger.LogDebug("TWAIN inicializado");
                        }
                        else
                        {
                            _logger.LogDebug("TWAIN ya funcionando - conservando estado");
                            await Task.Delay(500);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error con TWAIN");
                    }
                })
            };

            await Task.WhenAll(reinitTasks);

            // Obtener dispositivos con timeout
            _logger.LogDebug("Obteniendo dispositivos...");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            
            var deviceTasks = new List<Task<List<ScannerDevice>>>
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var wiaDevices = await _wiaService.GetAvailableDevicesAsync();
                        _logger.LogDebug("WIA: {Count} dispositivos", wiaDevices?.Count ?? 0);
                        return wiaDevices ?? new List<ScannerDevice>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error obteniendo dispositivos WIA");
                        return new List<ScannerDevice>();
                    }
                }, cts.Token),

                Task.Run(async () =>
                {
                    try
                    {
                        var twainDevices = await _twainService.GetAvailableDevicesAsync();
                        _logger.LogDebug("TWAIN: {Count} dispositivos", twainDevices?.Count ?? 0);
                        return twainDevices ?? new List<ScannerDevice>();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error obteniendo dispositivos TWAIN");
                        return new List<ScannerDevice>();
                    }
                }, cts.Token)
            };

            List<ScannerDevice>[] results;
            try
            {
                results = await Task.WhenAll(deviceTasks);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timeout obteniendo dispositivos - usando resultados parciales");
                results = new List<ScannerDevice>[2];
                
                for (int i = 0; i < deviceTasks.Count; i++)
                {
                    results[i] = deviceTasks[i].IsCompletedSuccessfully 
                        ? deviceTasks[i].Result 
                        : new List<ScannerDevice>();
                }
            }

            // Combinar resultados
            var allDevices = new List<ScannerDevice>();
            foreach (var deviceList in results.Where(r => r != null))
            {
                allDevices.AddRange(deviceList);
            }

            var uniqueDevices = RemoveDuplicateDevicesOptimized(allDevices);
            
            stopwatch.Stop();
            
            _logger.LogInformation("=== REFRESH OPTIMIZADO COMPLETADO EN {ElapsedMs}ms ===", stopwatch.ElapsedMilliseconds);
            _logger.LogInformation("Total encontrados: {Count} dispositivos únicos", uniqueDevices.Count);
            _logger.LogInformation("WIA: {WiaCount} | TWAIN: {TwainCount}", 
                results[0]?.Count ?? 0, results[1]?.Count ?? 0);
            
            foreach (var device in uniqueDevices)
            {
                _logger.LogInformation("📱 [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
            
            return uniqueDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico en refresh optimizado");
            return await GetFallbackDevices();
        }
    }

    public async Task<List<ScannerDevice>> ForceRefreshAllDevicesClean(bool forceRefresh = true)
    {
        _logger.LogInformation("=== REFRESH COMPLETO CON LIMPIEZA DE DESCONECTADOS ===");
        
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            // Usar refresh optimizado estándar
            var allDevices = await ForceRefreshAllDevices();
            
            // Filtrar dispositivos que no están realmente conectados
            var connectedDevices = new List<ScannerDevice>();
            
            foreach (var device in allDevices)
            {
                if (await QuickConnectivityCheckAsync(device))
                {
                    connectedDevices.Add(device);
                    _logger.LogDebug("Dispositivo conectado: {DisplayName}", device.DisplayName);
                }
                else
                {
                    _logger.LogDebug("Dispositivo desconectado eliminado: {DisplayName}", device.DisplayName);
                }
            }
            
            stopwatch.Stop();
            
            _logger.LogInformation("=== REFRESH LIMPIO COMPLETADO EN {ElapsedMs}ms ===", stopwatch.ElapsedMilliseconds);
            _logger.LogInformation("Dispositivos conectados: {Count} de {Total}", 
                connectedDevices.Count, allDevices.Count);
            
            return connectedDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en refresh con limpieza");
            return await ForceRefreshAllDevices();
        }
    }

    public void ClearConnectivityCache()
    {
        _connectivityCache.Clear();
        _logger.LogInformation("Cache de conectividad limpiado");
    }

    private List<ScannerDevice> RemoveDuplicateDevicesOptimized(List<ScannerDevice> devices)
    {
        var uniqueDevices = new List<ScannerDevice>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices.OrderBy(d => d.Type))
        {
            var deviceKey = $"{device.Type}|{CleanDeviceNameForComparison(device.Name)}";
        
            if (!seenKeys.Contains(deviceKey))
            {
                seenKeys.Add(deviceKey);
                uniqueDevices.Add(device);
                _logger.LogDebug("Agregado: [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
            else
            {
                _logger.LogDebug("Duplicado: [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
        }

        return uniqueDevices;
    }
    
    private string CleanDeviceNameForComparison(string name)
    {
        return name
            .Replace("WIA-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("TWAIN-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("TW-", "", StringComparison.OrdinalIgnoreCase)
            .Replace("USB", "", StringComparison.OrdinalIgnoreCase)
            .Replace("  ", " ")
            .Trim();
    }

    private async Task<List<ScannerDevice>> GetFallbackDevices()
    {
        try
        {
            _logger.LogWarning("Usando fallback básico...");
            
            var fallbackWia = await _wiaService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
            var fallbackTwain = await _twainService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
            
            var fallbackDevices = new List<ScannerDevice>();
            fallbackDevices.AddRange(fallbackWia);
            fallbackDevices.AddRange(fallbackTwain);
            
            var fallbackUnique = RemoveDuplicateDevicesOptimized(fallbackDevices);
            
            _logger.LogInformation("Fallback exitoso: {Count} dispositivos", fallbackUnique.Count);
            return fallbackUnique;
        }
        catch (Exception fallbackEx)
        {
            _logger.LogError(fallbackEx, "Error también en fallback básico");
            return new List<ScannerDevice>();
        }
    }
}