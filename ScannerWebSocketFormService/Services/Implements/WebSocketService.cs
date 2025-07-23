using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ScannerWebSocketFormService.Models;
using ScannerWebSocketFormService.Services.Interface;

namespace ScannerWebSocketFormService.Services.Implements;

public class WebSocketService : IWebSocketService, IDisposable
{
    private readonly ILogger<WebSocketService> _logger;
    private HttpListener? _httpListener;
    private readonly List<WebSocketClientInfo> _connectedClients = new();
    private bool _isListening = false;
    private bool _disposed = false;
    private Func<Task>? _scanHandler;
    private IImageProcessor? _imageProcessor;
    private readonly object _scanLock = new();
    
    // Estados de escaneo
    private bool _isScanningGlobally = false;
    private string? _currentScanningClientId = null;
    private DateTime? _lastScanStartTime = null;
    private readonly TimeSpan _scanTimeout = TimeSpan.FromMinutes(8);
    
    // Estados de verificación de conectividad
    private bool _isCheckingConnectivity = false;
    private string? _currentConnectivityCheckDevice = null;
    private DateTime? _connectivityCheckStartTime = null;
    
    // Rate limiting
    private readonly Dictionary<string, DateTime> _lastRequestTimes = new();
    private readonly TimeSpan _minRequestInterval = TimeSpan.FromSeconds(1);

    public bool IsListening => _isListening;
    public int ConnectedClientsCount => _connectedClients.Count;

    public WebSocketService(ILogger<WebSocketService> logger)
    {
        _logger = logger;
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ = Task.Run(MonitorTimeouts);
    }

    public void SetImageProcessor(IImageProcessor imageProcessor)
    {
        _imageProcessor = imageProcessor;
    }

    private void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case Microsoft.Win32.PowerModes.Suspend:
                _logger.LogWarning("Sistema entrando en suspensión - Limpiando estado");
                _ = Task.Run(() => CancelCurrentOperation("Sistema entrando en suspensión"));
                break;
                
            case Microsoft.Win32.PowerModes.Resume:
                _logger.LogInformation("Sistema reanudando desde suspensión");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(2000);
                    await HandleSystemResume();
                });
                break;
        }
    }

    private async Task HandleSystemResume()
    {
        try
        {
            await ResetAllStates("Sistema reanudado desde suspensión");
            
            await BroadcastMessageAsync(new {
                type = "system_resumed",
                message = "Sistema reanudado - Scanner reiniciado y listo"
            });
            
            _logger.LogInformation("Estado limpiado después de reanudación del sistema");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando reanudación del sistema");
        }
    }

    private async Task MonitorTimeouts()
    {
        while (!_disposed)
        {
            try
            {
                await Task.Delay(15000);
                
                lock (_scanLock)
                {
                    CheckScanTimeout();
                    CheckConnectivityTimeout();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en monitor de timeout");
            }
        }
    }

    private void CheckScanTimeout()
    {
        if (_isScanningGlobally && _lastScanStartTime.HasValue)
        {
            if (DateTime.Now - _lastScanStartTime.Value > _scanTimeout)
            {
                _logger.LogWarning("Timeout de escaneo detectado");
                _ = Task.Run(() => CancelCurrentOperation("Timeout de escaneo"));
            }
        }
    }

    private void CheckConnectivityTimeout()
    {
        if (_isCheckingConnectivity && _connectivityCheckStartTime.HasValue)
        {
            var checkDuration = DateTime.Now - _connectivityCheckStartTime.Value;
            if (checkDuration > TimeSpan.FromSeconds(10))
            {
                _logger.LogWarning("Timeout en verificación de conectividad");
                _ = Task.Run(() => TimeoutConnectivityCheck("Verificación de conectividad tomó demasiado tiempo"));
            }
        }
    }

    private async Task TimeoutConnectivityCheck(string reason)
    {
        try
        {
            ResetConnectivityState();

            await BroadcastMessageAsync(new {
                type = "connectivity_check_timeout",
                message = reason,
                timestamp = DateTime.Now
            });

            _logger.LogWarning("Verificación de conectividad terminada por timeout");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando timeout de conectividad");
        }
    }
    
    public async Task StartAsync()
    {
        try
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add("http://127.0.0.1:9000/");
            _httpListener.AuthenticationSchemes = AuthenticationSchemes.Anonymous;
            _httpListener.IgnoreWriteExceptions = true;
            _httpListener.Start();
            _isListening = true;

            _logger.LogInformation("Servidor WebSocket iniciado en 127.0.0.1:9000");
            _ = Task.Run(ListenForConnections);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error iniciando servidor WebSocket");
            throw;
        }
    }

    public async Task StopAsync()
    {
        _isListening = false;
        
        await CancelCurrentOperation("Servicio detenido");
        
        foreach (var clientInfo in _connectedClients.ToList())
        {
            try
            {
                if (clientInfo.WebSocket.State == WebSocketState.Open)
                {
                    await clientInfo.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Servicio cerrando", CancellationToken.None);
                }
            }
            catch { }
        }
        _connectedClients.Clear();
        
        _httpListener?.Stop();
        _httpListener?.Close();
    }

    public void RegisterScanHandler(Func<Task> scanHandler)
    {
        _scanHandler = scanHandler;
    }

    public async Task BroadcastMessageAsync(object message)
    {
        var clientsToRemove = new List<WebSocketClientInfo>();
        
        foreach (var clientInfo in _connectedClients.ToList())
        {
            if (clientInfo.WebSocket.State == WebSocketState.Open)
            {
                try
                {
                    await SendMessageAsync(clientInfo.WebSocket, message);
                    clientInfo.LastActivity = DateTime.Now;
                }
                catch
                {
                    clientsToRemove.Add(clientInfo);
                }
            }
            else
            {
                clientsToRemove.Add(clientInfo);
            }
        }
        
        foreach (var clientInfo in clientsToRemove)
        {
            await RemoveClient(clientInfo);
        }
    }

    public async Task NotifyConnectivityCheckStarted(string deviceName)
    {
        lock (_scanLock)
        {
            _isCheckingConnectivity = true;
            _currentConnectivityCheckDevice = deviceName;
            _connectivityCheckStartTime = DateTime.Now;
        }

        await BroadcastMessageAsync(new {
            type = "connectivity_check_started",
            deviceName = deviceName,
            message = $"⚡ Verificando conectividad de {deviceName}...",
            timestamp = DateTime.Now
        });

        _logger.LogInformation("Notificando inicio de verificación de conectividad: {DeviceName}", deviceName);
    }

    public async Task NotifyConnectivityCheckCompleted(string deviceName, bool isConnected, string details = "")
    {
        ResetConnectivityState();

        var messageType = isConnected ? "connectivity_check_success" : "connectivity_check_failed";
        var icon = isConnected ? "SI" : "NO";
        var status = isConnected ? "CONECTADO" : "NO CONECTADO";

        await BroadcastMessageAsync(new {
            type = messageType,
            deviceName = deviceName,
            isConnected = isConnected,
            message = $"{icon} {deviceName}: {status}",
            details = details,
            timestamp = DateTime.Now
        });

        _logger.LogInformation("Verificación de conectividad completada: {DeviceName} = {Status}", 
            deviceName, status);
    }

    public async Task NotifyDeviceSelectionError(string errorMessage)
    {
        await BroadcastMessageAsync(new {
            type = "device_selection_error",
            message = errorMessage,
            timestamp = DateTime.Now
        });
    }

    private void ResetConnectivityState()
    {
        lock (_scanLock)
        {
            _isCheckingConnectivity = false;
            _currentConnectivityCheckDevice = null;
            _connectivityCheckStartTime = null;
        }
    }

    private void ResetScanState()
    {
        lock (_scanLock)
        {
            _isScanningGlobally = false;
            _currentScanningClientId = null;
            _lastScanStartTime = null;
        }
    }

    private async Task ResetAllStates(string reason)
    {
        ResetScanState();
        ResetConnectivityState();

        _logger.LogInformation("Todos los estados limpiados: {Reason}", reason);
        
        await BroadcastMessageAsync(new {
            type = "all_states_reset",
            reason = reason,
            message = "Scanner listo para nuevo escaneo"
        });
    }

    private async Task ListenForConnections()
    {
        while (_isListening && _httpListener != null)
        {
            try
            {
                var context = await _httpListener.GetContextAsync();
                
                if (context.Request.IsWebSocketRequest)
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    var webSocket = wsContext.WebSocket;
                    
                    var clientInfo = new WebSocketClientInfo
                    {
                        Id = Guid.NewGuid().ToString(),
                        WebSocket = webSocket,
                        ConnectedAt = DateTime.Now,
                        LastActivity = DateTime.Now,
                        IpAddress = context.Request.RemoteEndPoint?.Address?.ToString() ?? "Unknown"
                    };
                    
                    _connectedClients.Add(clientInfo);
                    _logger.LogInformation("Cliente WebSocket conectado: {ClientId} desde {IP}. Total: {Count}", 
                        clientInfo.Id, clientInfo.IpAddress, _connectedClients.Count);
                    
                    await SendWelcomeMessage(clientInfo);
                    _ = Task.Run(() => HandleClient(clientInfo));
                }
                else
                {
                    await HandleHttpStatusRequest(context);
                }
            }
            catch (Exception ex)
            {
                if (_isListening)
                {
                    _logger.LogError(ex, "Error en listener");
                }
            }
        }
    }

    private async Task SendWelcomeMessage(WebSocketClientInfo clientInfo)
    {
        await SendMessageAsync(clientInfo.WebSocket, new {
            type = "connected",
            clientId = clientInfo.Id,
            message = "Conectado al servicio de scanner",
            isScanningGlobally = _isScanningGlobally,
            currentScanningClient = _currentScanningClientId,
            isCheckingConnectivity = _isCheckingConnectivity,
            currentConnectivityDevice = _currentConnectivityCheckDevice
        });
    }

    private async Task HandleHttpStatusRequest(HttpListenerContext context)
    {
        context.Response.StatusCode = 200;
        var responseString = $"Scanner WebSocket Service is running. Clients: {_connectedClients.Count}, Scanning: {_isScanningGlobally}, Checking: {_isCheckingConnectivity}";
        var buffer = Encoding.UTF8.GetBytes(responseString);
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        context.Response.Close();
    }

    private async Task HandleClient(WebSocketClientInfo clientInfo)
    {
        var buffer = new byte[4096];
        
        try
        {
            while (clientInfo.WebSocket.State == WebSocketState.Open)
            {
                var result = await clientInfo.WebSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await ProcessClientMessage(clientInfo, message);
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando cliente {ClientId}", clientInfo.Id);
        }
        finally
        {
            await RemoveClient(clientInfo);
        }
    }

    private async Task RemoveClient(WebSocketClientInfo clientInfo)
    {
        _connectedClients.Remove(clientInfo);
        
        if (_currentScanningClientId == clientInfo.Id)
        {
            _logger.LogWarning("Cliente que estaba escaneando se desconectó: {ClientId}", clientInfo.Id);
            await CancelCurrentOperation("Cliente desconectado durante escaneo");
        }
        
        _logger.LogInformation("Cliente desconectado: {ClientId}. Total: {Count}", clientInfo.Id, _connectedClients.Count);
    }
    
    private async Task ProcessClientMessage(WebSocketClientInfo clientInfo, string message)
    {
        try
        {
            if (!ValidateMessage(clientInfo, message, out var request))
                return;

            var action = request.GetProperty("action").GetString();
            
            if (IsRateLimited(clientInfo.Id))
            {
                await SendErrorMessage(clientInfo.WebSocket, "Demasiadas solicitudes. Espera un momento.", "rate_limited");
                return;
            }

            clientInfo.LastActivity = DateTime.Now;

            switch (action)
            {
                case "scan":
                    await HandleScanRequest(clientInfo);
                    break;
                    
                case "cancel_scan":
                    await HandleCancelScanRequest(clientInfo);
                    break;
                    
                case "reset_scan":
                    await HandleResetScanRequest(clientInfo);
                    break;
                    
                case "status":
                    await SendStatusMessage(clientInfo);
                    break;
                    
                case "ping":
                    await SendPongMessage(clientInfo);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error procesando mensaje de cliente {ClientId}", clientInfo.Id);
            await SendErrorMessage(clientInfo.WebSocket, "Error interno procesando solicitud");
        }
    }

    private bool ValidateMessage(WebSocketClientInfo clientInfo, string message, out JsonElement request)
    {
        request = default;

        if (message.Length > 4096)
        {
            _ = SendErrorMessage(clientInfo.WebSocket, "Mensaje demasiado largo");
            return false;
        }

        try
        {
            request = JsonSerializer.Deserialize<JsonElement>(message);
        }
        catch (JsonException)
        {
            _ = SendErrorMessage(clientInfo.WebSocket, "Formato JSON inválido");
            return false;
        }

        if (!request.TryGetProperty("action", out var actionElement))
        {
            _ = SendErrorMessage(clientInfo.WebSocket, "Acción requerida");
            return false;
        }

        var action = actionElement.GetString();
        var allowedActions = new[] { "scan", "cancel_scan", "reset_scan", "status", "ping" };
        
        if (string.IsNullOrEmpty(action) || !allowedActions.Contains(action))
        {
            _ = SendErrorMessage(clientInfo.WebSocket, "Acción no permitida");
            return false;
        }

        return true;
    }
    
    private bool IsRateLimited(string clientId)
    {
        if (_lastRequestTimes.TryGetValue(clientId, out var lastTime))
        {
            if (DateTime.Now - lastTime < _minRequestInterval)
            {
                return true;
            }
        }
    
        _lastRequestTimes[clientId] = DateTime.Now;
        return false;
    }

    private string GetStatusMessage()
    {
        if (_isScanningGlobally)
            return $"Scanner ocupado por cliente {_currentScanningClientId}";
        
        if (_isCheckingConnectivity)
            return $"Verificando conectividad de {_currentConnectivityCheckDevice ?? "dispositivo"}...";
        
        return "Scanner listo para escanear";
    }

    private async Task SendStatusMessage(WebSocketClientInfo clientInfo)
    {
        await SendMessageAsync(clientInfo.WebSocket, new {
            type = "status",
            isReady = !_isScanningGlobally && !_isCheckingConnectivity,
            connectedClients = _connectedClients.Count,
            isScanningGlobally = _isScanningGlobally,
            isCheckingConnectivity = _isCheckingConnectivity,
            currentScanningClient = _currentScanningClientId,
            currentConnectivityDevice = _currentConnectivityCheckDevice,
            clientId = clientInfo.Id,
            message = GetStatusMessage()
        });
    }

    private async Task SendPongMessage(WebSocketClientInfo clientInfo)
    {
        await SendMessageAsync(clientInfo.WebSocket, new {
            type = "pong",
            timestamp = DateTime.Now,
            isReady = !_isScanningGlobally && !_isCheckingConnectivity
        });
    }

    private async Task SendErrorMessage(WebSocket webSocket, string message, string type = "error")
    {
        await SendMessageAsync(webSocket, new {
            type = type,
            message = message
        });
    }

    private async Task HandleScanRequest(WebSocketClientInfo clientInfo)
    {
        lock (_scanLock)
        {
            if (_isScanningGlobally)
            {
                _ = SendMessageAsync(clientInfo.WebSocket, new {
                    type = "scan_blocked",
                    message = $"Scanner ocupado por otro cliente: {_currentScanningClientId}",
                    currentScanningClient = _currentScanningClientId
                });
                return;
            }

            if (_isCheckingConnectivity)
            {
                _ = SendMessageAsync(clientInfo.WebSocket, new {
                    type = "scan_blocked",
                    message = $"Verificando conectividad de {_currentConnectivityCheckDevice}. Espera un momento...",
                    reason = "connectivity_check"
                });
                return;
            }
            
            _isScanningGlobally = true;
            _currentScanningClientId = clientInfo.Id;
            _lastScanStartTime = DateTime.Now;
        }

        _logger.LogInformation("Solicitud de escaneo aceptada de cliente {ClientId}", clientInfo.Id);
        
        _imageProcessor?.ResetCancelFlag();
        
        await BroadcastScanStateChanged(true, clientInfo.Id, $"Escaneo iniciado por cliente {clientInfo.Id}");

        if (_scanHandler != null)
        {
            try
            {
                await _scanHandler();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ejecutando scan handler");
                await CancelCurrentOperation("Error en scan handler");
            }
        }
    }

    private async Task HandleCancelScanRequest(WebSocketClientInfo clientInfo)
    {
        if (_currentScanningClientId == clientInfo.Id)
        {
            _logger.LogInformation("Cancelación de escaneo solicitada por cliente {ClientId}", clientInfo.Id);
            await CancelCurrentOperation($"Cancelado por cliente {clientInfo.Id}");
        }
        else
        {
            await SendMessageAsync(clientInfo.WebSocket, new {
                type = "cancel_denied",
                message = $"Solo el cliente {_currentScanningClientId} puede cancelar el escaneo"
            });
        }
    }

    private async Task HandleResetScanRequest(WebSocketClientInfo clientInfo)
    {
        if (_currentScanningClientId == null || _currentScanningClientId == clientInfo.Id)
        {
            _logger.LogInformation("Reset de escaneo solicitado por cliente {ClientId}", clientInfo.Id);
            await ResetAllStates($"Reset solicitado por cliente {clientInfo.Id}");
        }
        else
        {
            await SendMessageAsync(clientInfo.WebSocket, new {
                type = "reset_denied",
                message = $"Solo el cliente {_currentScanningClientId} puede resetear el escaneo"
            });
        }
    }

    private async Task CancelCurrentOperation(string reason)
    {
        string? previousClient = null;
        
        lock (_scanLock)
        {
            if (_isScanningGlobally)
            {
                previousClient = _currentScanningClientId;
                ResetScanState();
            }
        }

        if (previousClient != null && _imageProcessor != null)
        {
            await _imageProcessor.CancelScanAsync();
        }

        _logger.LogInformation("Operación cancelada: {Reason}", reason);
        
        await BroadcastMessageAsync(new {
            type = "scan_cancelled",
            reason = reason,
            previousClient = previousClient,
            message = "Escaneo cancelado - Scanner listo para nuevo escaneo"
        });
    }

    private async Task BroadcastScanStateChanged(bool isScanning, string clientId, string message)
    {
        await BroadcastMessageAsync(new {
            type = "scan_state_changed",
            isScanningGlobally = isScanning,
            scanningClientId = clientId,
            message = message
        });
    }

    public async Task ForceResetScanningState(string reason)
    {
        await ResetAllStates(reason);
    }

    private async Task SendMessageAsync(WebSocket webSocket, object message)
    {
        if (webSocket.State == WebSocketState.Open)
        {
            var json = JsonSerializer.Serialize(message);
            var buffer = Encoding.UTF8.GetBytes(json);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None);
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
            
            _ = StopAsync();
            _disposed = true;
        }
    }
}