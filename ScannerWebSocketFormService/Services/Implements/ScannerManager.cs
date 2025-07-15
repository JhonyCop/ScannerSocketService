using Microsoft.Extensions.Logging;
using ScannerWebSocketFormService.Models;
using ScannerWebSocketFormService.Services.Interface;

namespace ScannerWebSocketFormService.Services.Implements;

public class ScannerManager : IScannerManager
{
    private readonly ITwainService _twainService;
    private readonly IScannerService _wiaService;
    private readonly WiaService _wiaServiceTyped; //  Referencia tipada para métodos específicos
    private readonly ILogger<ScannerManager> _logger;
    private readonly object _refreshLock = new();
    private DateTime _lastFullRefresh = DateTime.MinValue;
    private readonly TimeSpan _minimumRefreshInterval = TimeSpan.FromSeconds(2); // ⚡ Reducido de 3 a 2 segundos

    // Cache de dispositivos para comparación
    private List<ScannerDevice> _lastKnownDevices = new();
    private DateTime _lastCacheUpdate = DateTime.MinValue;

    //  Cache de conectividad para evitar verificaciones repetitivas
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
        else
        {
            try
            {
                var wiaDevices = await _wiaService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
                var twainDevices = await _twainService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
            
                var allDevices = new List<ScannerDevice>();
                allDevices.AddRange(wiaDevices);
                allDevices.AddRange(twainDevices);
            
                return RemoveDuplicatesConservative(allDevices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en obtención rápida de dispositivos");
                return new List<ScannerDevice>();
            }
        }
    }

    
    

    //  Verificación rápida de conectividad para un dispositivo específico
    public async Task<bool> QuickConnectivityCheckAsync(ScannerDevice device)
    {
        if (device == null) return false;

        var deviceKey = $"{device.Type}_{device.Id ?? device.Name}";
        
        // Verificar cache primero
        if (_connectivityCache.TryGetValue(deviceKey, out var cached))
        {
            if (DateTime.Now - cached.CheckTime < _connectivityCacheExpiry)
            {
                _logger.LogDebug("⚡ Cache conectividad para {DeviceName}: {IsConnected}", device.Name, cached.IsConnected);
                return cached.IsConnected;
            }
        }

        _logger.LogInformation("⚡ Verificación rápida de conectividad: {DeviceName} ({Type})", device.Name, device.Type);
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
                // Para TWAIN, verificación más básica (TWAIN es más frágil)
                isConnected = await QuickTwainConnectivityCheck(device);
            }

            // Guardar en cache
            _connectivityCache[deviceKey] = (isConnected, DateTime.Now);

            stopwatch.Stop();
            _logger.LogInformation("⚡ Conectividad {DeviceName}: {Result} ({ElapsedMs}ms)", 
                device.Name, 
                isConnected ? " CONECTADO" : " DESCONECTADO", 
                stopwatch.ElapsedMilliseconds);

            return isConnected;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, " Error verificando conectividad {DeviceName} ({ElapsedMs}ms)", 
                device.Name, stopwatch.ElapsedMilliseconds);
            
            // En caso de error, guardar como no conectado en cache
            _connectivityCache[deviceKey] = (false, DateTime.Now);
            return false;
        }
    }


    //  Verificación rápida para TWAIN (más conservadora)
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

    //  OPTIMIZADO: StartScan con verificación previa de conectividad
    public async Task<bool> StartScanAsync(ScannerDevice device)
    {
        try
        {
            _logger.LogInformation("🚀 Iniciando escaneo con verificación previa: {DeviceName} (Tipo: {Type})", 
                device.Name, device.Type);

            //  NUEVA: Verificación rápida de conectividad ANTES del escaneo
            _logger.LogInformation("⚡ Verificando conectividad previa al escaneo...");
            var isConnected = await QuickConnectivityCheckAsync(device);

            if (!isConnected)
            {
                _logger.LogWarning(" Dispositivo {DeviceName} no está conectado - abortando escaneo", device.Name);
                return false;
            }

            _logger.LogInformation(" Dispositivo {DeviceName} verificado como conectado - procediendo con escaneo", device.Name);

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
            _logger.LogError(ex, " Error iniciando escaneo con dispositivo {DeviceName}", device.Name);
            return false;
        }
    }

    public async Task<List<ScannerDevice>> RefreshDevicesForUI()
    {
        _logger.LogInformation("=== REFRESH DE DISPOSITIVOS PARA UI (OPTIMIZADO) ===");
    
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            // Obtener dispositivos de ambos servicios con timeouts reducidos
            var deviceTasks = new List<Task<List<ScannerDevice>>>();

            // WIA con timeout
            deviceTasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)); // ⚡ 8 segundos máximo
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
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6)); // ⚡ 6 segundos máximo
                    return await _twainService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error obteniendo dispositivos TWAIN");
                    return new List<ScannerDevice>();
                }
            }));

            // Esperar resultados con timeout global
            var results = await Task.WhenAll(deviceTasks);
            var wiaDevices = results[0];
            var twainDevices = results[1];
        
            _logger.LogInformation("WIA encontrados: {Count}", wiaDevices.Count);
            _logger.LogInformation("TWAIN encontrados: {Count}", twainDevices.Count);
        
            // Combinar dispositivos
            var allDevices = new List<ScannerDevice>();
            allDevices.AddRange(wiaDevices);
            allDevices.AddRange(twainDevices);
        
            // Eliminar duplicados conservadoramente
            var uniqueDevices = RemoveDuplicatesConservative(allDevices);

            stopwatch.Stop();
            _logger.LogInformation("=== REFRESH COMPLETADO EN {ElapsedMs}ms: {Count} dispositivos ===", 
                stopwatch.ElapsedMilliseconds, uniqueDevices.Count);
        
            foreach (var device in uniqueDevices)
            {
                _logger.LogInformation("  📱 [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
        
            return uniqueDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error en refresh optimizado de dispositivos");
            return new List<ScannerDevice>();
        }
    }
    
    private List<ScannerDevice> RemoveDuplicatesConservative(List<ScannerDevice> devices)
    {
        var uniqueDevices = new List<ScannerDevice>();
        var seenDevices = new HashSet<string>();

        foreach (var device in devices)
        {
            var deviceKey = $"{device.Type}|{device.Name}|{device.Id ?? "NO_ID"}";
        
            _logger.LogDebug("Evaluando dispositivo: Key='{Key}', Display='{Display}'", deviceKey, device.DisplayName);
        
            if (!seenDevices.Contains(deviceKey))
            {
                seenDevices.Add(deviceKey);
                uniqueDevices.Add(device);
                _logger.LogDebug("   Agregado: [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
            else
            {
                _logger.LogDebug("   Duplicado exacto eliminado: [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
        }
    
        return uniqueDevices;
    }

    public async Task<List<ScannerDevice>> ForceRefreshAllDevices()
    {
        _logger.LogInformation("=== REFRESH FORZADO OPTIMIZADO DE TODOS LOS DISPOSITIVOS ===");
        
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            //  PASO 1: Limpiar cachés rápidamente
            _logger.LogDebug("Paso 1: Limpiando cachés...");
            _connectivityCache.Clear(); //  Limpiar cache de conectividad
            
            if (_wiaService is WiaService wiaImpl)
            {
                wiaImpl.ClearDeviceCache();
                _logger.LogDebug(" Caché WIA limpiado");
            }

            //  PASO 2: Liberación rápida de recursos
            _logger.LogDebug("Paso 2: Liberando recursos...");
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(400); // ⚡ Reducido de 500 a 400ms

            //  PASO 3: Reinicializar solo WIA (TWAIN se mantiene si funciona)
            _logger.LogDebug("Paso 3: Reinicializando servicios...");
            
            var reinitTasks = new List<Task>();
            
            // Reinicializar WIA
            reinitTasks.Add(Task.Run(async () =>
            {
                try
                {
                    await _wiaService.InitializeAsync();
                    await Task.Delay(600); // ⚡ Reducido de 800 a 600ms
                    _logger.LogDebug(" WIA reinicializado");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, " Error reinicializando WIA");
                }
            }));

            // TWAIN más conservador
            reinitTasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (!_twainService.IsInitialized)
                    {
                        _logger.LogDebug("TWAIN no inicializado - inicializando...");
                        await _twainService.InitializeAsync();
                        await Task.Delay(1000); // ⚡ Reducido de 1200 a 1000ms
                        _logger.LogDebug(" TWAIN inicializado");
                    }
                    else
                    {
                        _logger.LogDebug(" TWAIN ya funcionando - conservando estado");
                        await Task.Delay(500); // ⚡ Reducido de 600 a 500ms
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, " Error con TWAIN");
                }
            }));

            await Task.WhenAll(reinitTasks);

            //  PASO 4: Obtener dispositivos con timeout más agresivo
            _logger.LogDebug("Paso 4: Obteniendo dispositivos...");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12)); // ⚡ Reducido de 15 a 12 segundos
            
            var deviceTasks = new List<Task<List<ScannerDevice>>>();
            
            deviceTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var wiaDevices = await _wiaService.GetAvailableDevicesAsync();
                    _logger.LogDebug(" WIA: {Count} dispositivos", wiaDevices?.Count ?? 0);
                    return wiaDevices ?? new List<ScannerDevice>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, " Error obteniendo dispositivos WIA");
                    return new List<ScannerDevice>();
                }
            }, cts.Token));

            deviceTasks.Add(Task.Run(async () =>
            {
                try
                {
                    var twainDevices = await _twainService.GetAvailableDevicesAsync();
                    _logger.LogDebug(" TWAIN: {Count} dispositivos", twainDevices?.Count ?? 0);
                    return twainDevices ?? new List<ScannerDevice>();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, " Error obteniendo dispositivos TWAIN");
                    return new List<ScannerDevice>();
                }
            }, cts.Token));

            List<ScannerDevice>[] results;
            try
            {
                results = await Task.WhenAll(deviceTasks);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(" Timeout obteniendo dispositivos - usando resultados parciales");
                results = new List<ScannerDevice>[2];
                
                for (int i = 0; i < deviceTasks.Count; i++)
                {
                    if (deviceTasks[i].IsCompletedSuccessfully)
                    {
                        results[i] = deviceTasks[i].Result;
                    }
                    else
                    {
                        results[i] = new List<ScannerDevice>();
                    }
                }
            }

            //  PASO 5: Combinar resultados
            var allDevices = new List<ScannerDevice>();
            
            foreach (var deviceList in results)
            {
                if (deviceList != null)
                {
                    allDevices.AddRange(deviceList);
                }
            }

            var uniqueDevices = RemoveDuplicateDevicesOptimized(allDevices);
            
            stopwatch.Stop();
            
            _logger.LogInformation("=== REFRESH OPTIMIZADO COMPLETADO EN {ElapsedMs}ms ===", stopwatch.ElapsedMilliseconds);
            _logger.LogInformation("   Total encontrados: {Count} dispositivos únicos", uniqueDevices.Count);
            _logger.LogInformation("   WIA: {WiaCount} | TWAIN: {TwainCount}", 
                results[0]?.Count ?? 0, results[1]?.Count ?? 0);
            
            foreach (var device in uniqueDevices)
            {
                _logger.LogInformation("  📱 [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
            
            return uniqueDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error crítico en refresh optimizado");
            
            try
            {
                _logger.LogWarning(" Usando fallback básico...");
                
                var fallbackWia = await _wiaService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
                var fallbackTwain = await _twainService.GetAvailableDevicesAsync() ?? new List<ScannerDevice>();
                
                var fallbackDevices = new List<ScannerDevice>();
                fallbackDevices.AddRange(fallbackWia);
                fallbackDevices.AddRange(fallbackTwain);
                
                var fallbackUnique = RemoveDuplicateDevicesOptimized(fallbackDevices);
                
                _logger.LogInformation(" Fallback exitoso: {Count} dispositivos", fallbackUnique.Count);
                return fallbackUnique;
            }
            catch (Exception fallbackEx)
            {
                _logger.LogError(fallbackEx, " Error también en fallback básico");
                return new List<ScannerDevice>();
            }
        }
    }

    //  Implementación del método faltante
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
                // Verificar conectividad para cada dispositivo
                if (await QuickConnectivityCheckAsync(device))
                {
                    connectedDevices.Add(device);
                    _logger.LogDebug(" Dispositivo conectado: {DisplayName}", device.DisplayName);
                }
                else
                {
                    _logger.LogDebug(" Dispositivo desconectado eliminado: {DisplayName}", device.DisplayName);
                }
            }
            
            stopwatch.Stop();
            
            _logger.LogInformation("=== REFRESH LIMPIO COMPLETADO EN {ElapsedMs}ms ===", stopwatch.ElapsedMilliseconds);
            _logger.LogInformation(" Dispositivos conectados: {Count} de {Total}", 
                connectedDevices.Count, allDevices.Count);
            
            return connectedDevices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, " Error en refresh con limpieza");
            
            // Fallback al refresh estándar
            return await ForceRefreshAllDevices();
        }
    }

    private List<ScannerDevice> RemoveDuplicateDevicesOptimized(List<ScannerDevice> devices)
    {
        var uniqueDevices = new List<ScannerDevice>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in devices.OrderBy(d => d.Type)) // Priorizar orden por tipo
        {
            var deviceKey = $"{device.Type}|{CleanDeviceNameForComparison(device.Name)}";
        
            if (!seenKeys.Contains(deviceKey))
            {
                seenKeys.Add(deviceKey);
                uniqueDevices.Add(device);
                _logger.LogDebug(" Agregado: [{Type}] {DisplayName}", device.Type, device.DisplayName);
            }
            else
            {
                _logger.LogDebug(" Duplicado: [{Type}] {DisplayName}", device.Type, device.DisplayName);
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


    public void ClearConnectivityCache()
    {
        _connectivityCache.Clear();
        _logger.LogInformation(" Cache de conectividad limpiado");
    }


}