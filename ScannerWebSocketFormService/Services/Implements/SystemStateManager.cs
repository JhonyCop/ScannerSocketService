using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using ScannerWebSocketFormService.Models;

namespace ScannerWebSocketFormService.Services.Implements;

public class SystemStateManager : IDisposable
{
    private readonly ILogger<SystemStateManager> _logger;
    private readonly List<Func<Task>> _suspendHandlers = new();
    private readonly List<Func<Task>> _resumeHandlers = new();
    private readonly List<Func<Task>> _lockHandlers = new();
    private readonly List<Func<Task>> _unlockHandlers = new();
    private bool _disposed = false;
    private bool _isSystemSuspended = false;
    private bool _isSessionLocked = false;
    private DateTime? _suspendTime = null;
    private DateTime? _resumeTime = null;

    public bool IsSystemSuspended => _isSystemSuspended;
    public bool IsSessionLocked => _isSessionLocked;
    public TimeSpan? SuspendDuration => _suspendTime.HasValue && _resumeTime.HasValue 
        ? _resumeTime.Value - _suspendTime.Value 
        : null;

    public event EventHandler<PowerModeChangedEventArgs>? PowerModeChanged;
    public event EventHandler<SessionSwitchEventArgs>? SessionSwitchOccurred;

    public SystemStateManager(ILogger<SystemStateManager> logger)
    {
        _logger = logger;
        RegisterSystemEvents();
    }

    private void RegisterSystemEvents()
    {
        try
        {
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;
            _logger.LogInformation("Eventos del sistema registrados correctamente");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registrando eventos del sistema");
            throw;
        }
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        try
        {
            _logger.LogInformation("Cambio de modo de energía detectado: {Mode}", e.Mode);
            
            switch (e.Mode)
            {
                case PowerModes.Suspend:
                    await HandleSuspend();
                    break;
                    
                case PowerModes.Resume:
                    await HandleResume();
                    break;
            }

            PowerModeChanged?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando cambio de modo de energía");
        }
    }

    private async void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        try
        {
            _logger.LogInformation("Cambio de sesión detectado: {Reason}", e.Reason);
            
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock:
                    await HandleSessionLock();
                    break;
                    
                case SessionSwitchReason.SessionUnlock:
                    await HandleSessionUnlock();
                    break;
            }

            SessionSwitchOccurred?.Invoke(this, e);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error manejando cambio de sesión");
        }
    }

    private async Task HandleSuspend()
    {
        _isSystemSuspended = true;
        _suspendTime = DateTime.Now;
        _resumeTime = null;
        
        _logger.LogWarning("Sistema entrando en suspensión a las {Time}", _suspendTime);
        
        // Ejecutar handlers de suspensión
        await ExecuteHandlers(_suspendHandlers, "suspensión");
    }

    private async Task HandleResume()
    {
        _resumeTime = DateTime.Now;
        var suspendDuration = SuspendDuration;
        
        _logger.LogInformation("Sistema reanudando a las {Time} (suspendido por {Duration})", 
            _resumeTime, suspendDuration?.ToString(@"hh\:mm\:ss") ?? "tiempo desconocido");
        
        // Esperar estabilización del sistema
        await Task.Delay(2000);
        
        // Ejecutar handlers de reanudación
        await ExecuteHandlers(_resumeHandlers, "reanudación");
        
        _isSystemSuspended = false;
    }

    private async Task HandleSessionLock()
    {
        _isSessionLocked = true;
        _logger.LogInformation("Sesión bloqueada");
        
        // Ejecutar handlers de bloqueo
        await ExecuteHandlers(_lockHandlers, "bloqueo de sesión");
    }

    private async Task HandleSessionUnlock()
    {
        _logger.LogInformation("Sesión desbloqueada");
        
        // Esperar un momento para estabilización
        await Task.Delay(1000);
        
        // Ejecutar handlers de desbloqueo
        await ExecuteHandlers(_unlockHandlers, "desbloqueo de sesión");
        
        _isSessionLocked = false;
    }

    private async Task ExecuteHandlers(List<Func<Task>> handlers, string eventType)
    {
        foreach (var handler in handlers.ToList())
        {
            try
            {
                await handler();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error ejecutando handler de {EventType}", eventType);
            }
        }
    }

    // Métodos públicos para registrar handlers
    public void RegisterSuspendHandler(Func<Task> handler)
    {
        _suspendHandlers.Add(handler);
        _logger.LogDebug("Handler de suspensión registrado");
    }

    public void RegisterResumeHandler(Func<Task> handler)
    {
        _resumeHandlers.Add(handler);
        _logger.LogDebug("Handler de reanudación registrado");
    }

    public void RegisterSessionLockHandler(Func<Task> handler)
    {
        _lockHandlers.Add(handler);
        _logger.LogDebug("Handler de bloqueo de sesión registrado");
    }

    public void RegisterSessionUnlockHandler(Func<Task> handler)
    {
        _unlockHandlers.Add(handler);
        _logger.LogDebug("Handler de desbloqueo de sesión registrado");
    }

    // Métodos para remover handlers
    public void UnregisterSuspendHandler(Func<Task> handler)
    {
        _suspendHandlers.Remove(handler);
    }

    public void UnregisterResumeHandler(Func<Task> handler)
    {
        _resumeHandlers.Remove(handler);
    }

    public void UnregisterSessionLockHandler(Func<Task> handler)
    {
        _lockHandlers.Remove(handler);
    }

    public void UnregisterSessionUnlockHandler(Func<Task> handler)
    {
        _unlockHandlers.Remove(handler);
    }

    // Método para forzar limpieza de estado
    public async Task ForceSystemStateCleanup(string reason)
    {
        _logger.LogWarning("Forzando limpieza de estado del sistema: {Reason}", reason);
        
        try
        {
            // Ejecutar handlers de suspensión para limpiar estado
            await ExecuteHandlers(_suspendHandlers, "limpieza forzada");
            
            // Resetear flags
            _isSystemSuspended = false;
            _isSessionLocked = false;
            
            _logger.LogInformation("Limpieza forzada de estado completada");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante limpieza forzada de estado");
        }
    }

    // Información del estado actual
    public SystemStateInfo GetCurrentState()
    {
        return new SystemStateInfo
        {
            IsSystemSuspended = _isSystemSuspended,
            IsSessionLocked = _isSessionLocked,
            SuspendTime = _suspendTime,
            ResumeTime = _resumeTime,
            SuspendDuration = SuspendDuration,
            SuspendHandlerCount = _suspendHandlers.Count,
            ResumeHandlerCount = _resumeHandlers.Count,
            LockHandlerCount = _lockHandlers.Count,
            UnlockHandlerCount = _unlockHandlers.Count
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            try
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                _logger.LogInformation("Eventos del sistema desregistrados");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error desregistrando eventos del sistema");
            }

            _suspendHandlers.Clear();
            _resumeHandlers.Clear();
            _lockHandlers.Clear();
            _unlockHandlers.Clear();

            _disposed = true;
        }
    }
}

