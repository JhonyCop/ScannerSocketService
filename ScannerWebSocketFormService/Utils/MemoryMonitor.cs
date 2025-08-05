using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ScannerWebSocketFormService.Utils;

public class MemoryMonitor
{
    private readonly ILogger<MemoryMonitor> _logger;
    private readonly System.Threading.Timer _monitorTimer;
    private long _lastWorkingSet = 0;
    private const long WARNING_THRESHOLD_MB = 50;
    private const long CRITICAL_THRESHOLD_MB = 80;
        
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr proc, int min, int max);
        
        public MemoryMonitor(ILogger<MemoryMonitor> logger)
        {
            _logger = logger;
            
            // Configurar GC para modo servidor (más agresivo)
            GCSettings.LatencyMode = GCLatencyMode.Interactive;
            
            // Iniciar monitoreo cada 10 segundos
            _monitorTimer = new System.Threading.Timer(MonitorMemory, null, 
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
        
        private void MonitorMemory(object? state)
        {
            try
            {
                var process = Process.GetCurrentProcess();
                var workingSetMB = process.WorkingSet64 / (1024 * 1024);
                var privateMemoryMB = process.PrivateMemorySize64 / (1024 * 1024);
                var managedMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
                
                // Log si hay cambio significativo
                if (Math.Abs(workingSetMB - _lastWorkingSet) > 5)
                {
                    _logger.LogInformation(" Memoria - Working: {Working}MB | Private: {Private}MB | Managed: {Managed}MB",
                        workingSetMB, privateMemoryMB, managedMemoryMB);
                    _lastWorkingSet = workingSetMB;
                }
                
                // Acciones según umbral
                if (workingSetMB > CRITICAL_THRESHOLD_MB)
                {
                    _logger.LogWarning(" MEMORIA CRÍTICA: {Memory}MB - Ejecutando limpieza agresiva", workingSetMB);
                    ExecuteAggressiveCleanup();
                }
                else if (workingSetMB > WARNING_THRESHOLD_MB)
                {
                    _logger.LogWarning(" Memoria alta: {Memory}MB - Ejecutando limpieza normal", workingSetMB);
                    ExecuteNormalCleanup();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoreando memoria");
            }
        }
        
        public void ExecuteNormalCleanup()
        {
            var beforeMB = GetWorkingSetMB();
            
            // Recolección normal
            GC.Collect(2, GCCollectionMode.Optimized, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Optimized, true);
            
            var afterMB = GetWorkingSetMB();
            
            if (beforeMB - afterMB > 5)
            {
                _logger.LogInformation("🧹 Limpieza normal: {Before}MB → {After}MB (liberados: {Freed}MB)",
                    beforeMB, afterMB, beforeMB - afterMB);
            }
        }
        
        public void ExecuteAggressiveCleanup()
        {
            var beforeMB = GetWorkingSetMB();
            
            try
            {
                // 1. Recolección forzada completa
                GC.Collect(2, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true);
                
                // 2. Compactar LOH (Large Object Heap)
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true);
                
                // 3. Reducir working set del proceso
                TrimWorkingSet();
                
                var afterMB = GetWorkingSetMB();
                
                _logger.LogInformation(" Limpieza agresiva: {Before}MB → {After}MB (liberados: {Freed}MB)",
                    beforeMB, afterMB, beforeMB - afterMB);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error en limpieza agresiva");
            }
        }
        
        private void TrimWorkingSet()
        {
            try
            {
                // Forzar al SO a reducir el working set
                SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, -1, -1);
                _logger.LogDebug("Working set reducido");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reduciendo working set");
            }
        }
        
        public long GetWorkingSetMB()
        {
            return Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
        }
        
        public long GetPrivateMemoryMB()
        {
            return Process.GetCurrentProcess().PrivateMemorySize64 / (1024 * 1024);
        }
        
        public long GetManagedMemoryMB()
        {
            return GC.GetTotalMemory(false) / (1024 * 1024);
        }
        
        public MemoryStatus GetMemoryStatus()
        {
            var process = Process.GetCurrentProcess();
            return new MemoryStatus
            {
                WorkingSetMB = process.WorkingSet64 / (1024 * 1024),
                PrivateMemoryMB = process.PrivateMemorySize64 / (1024 * 1024),
                ManagedMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024),
                Gen0Collections = GC.CollectionCount(0),
                Gen1Collections = GC.CollectionCount(1),
                Gen2Collections = GC.CollectionCount(2),
                TotalMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024)
            };
        }
        
        public void ForceCleanup(string reason)
        {
            _logger.LogInformation("🔧 Limpieza forzada solicitada: {Reason}", reason);
            ExecuteAggressiveCleanup();
        }
        
        public void Dispose()
        {
            _monitorTimer?.Dispose();
            ExecuteAggressiveCleanup();
        }
}