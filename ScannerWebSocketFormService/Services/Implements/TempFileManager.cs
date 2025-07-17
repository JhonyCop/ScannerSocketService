using Microsoft.Extensions.Logging;
using ScannerWebSocketFormService.Services.Interface;

namespace ScannerWebSocketFormService.Services.Implements;

public class TempFileManager : ITempFileManager, IDisposable
{
    private readonly ILogger<TempFileManager> _logger;
        private readonly HashSet<string> _tempFilesCreated = new();
        private string _tempFolder = string.Empty;
        private bool _disposed = false;

        public string TempFolder => _tempFolder;

        public TempFileManager(ILogger<TempFileManager> logger)
        {
            _logger = logger;
        }

        public void Initialize()
        {
            try
            {
                _tempFolder = Path.Combine(Path.GetTempPath(), "BrotherScan_" + Environment.ProcessId);
                
                if (Directory.Exists(_tempFolder))
                {
                    try
                    {
                        Directory.Delete(_tempFolder, true);
                        _logger.LogInformation("Carpeta temporal anterior eliminada completamente");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error eliminando carpeta anterior");
                    }
                }

                Directory.CreateDirectory(_tempFolder);
                _logger.LogInformation("Nueva carpeta temporal creada: {TempFolder}", _tempFolder);

                ValidatePermissions();
                ValidateSpace();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error crítico configurando carpeta temporal");
                throw;
            }
        }

        public void AddTempFile(string filePath)
        {
            _tempFilesCreated.Add(filePath);
        }

        public void CleanupTempFiles()
        {
            try
            {
                var filesToDelete = _tempFilesCreated.ToList();
                foreach (var file in filesToDelete)
                {
                    if (File.Exists(file))
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                            _logger.LogInformation("Archivo temporal eliminado: {FileName}", Path.GetFileName(file));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error eliminando {FileName}", Path.GetFileName(file));
                        }
                    }
                    _tempFilesCreated.Remove(file);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error limpiando archivos temporales");
            }
        }

        public void CleanupAll()
        {
            try
            {
                CleanupTempFiles();

                if (Directory.Exists(_tempFolder))
                {
                    var files = Directory.GetFiles(_tempFolder, "*.*");
                    _logger.LogInformation("Eliminando {Count} archivos temporales...", files.Length);

                    foreach (var file in files)
                    {
                        try
                        {
                            File.SetAttributes(file, FileAttributes.Normal);
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "No se pudo eliminar {FileName}", Path.GetFileName(file));
                        }
                    }
                }

                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error en limpieza total");
            }
        }

        private void ValidatePermissions()
        {
            var testFile = Path.Combine(_tempFolder, "test_permisos.tmp");
            try
            {
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
                _logger.LogInformation("Permisos de escritura verificados");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ERROR CRÍTICO: Sin permisos de escritura");
                throw;
            }
        }

        private void ValidateSpace()
        {
            var tempPath = Path.GetPathRoot(_tempFolder);
            if (!string.IsNullOrEmpty(tempPath))
            {
                var drive = new DriveInfo(tempPath);
                long availableMB = drive.AvailableFreeSpace / (1024 * 1024);
                
                if (availableMB < 500)
                {
                    _logger.LogError("ESPACIO INSUFICIENTE: {AvailableMB} MB (mínimo 500 MB)", availableMB);
                    throw new InvalidOperationException($"Espacio insuficiente: {availableMB} MB");
                }
                else
                {
                    _logger.LogInformation("Espacio disponible: {AvailableMB} MB", availableMB);
                }
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                CleanupAll();
                
                if (Directory.Exists(_tempFolder))
                {
                    try
                    {
                        Directory.Delete(_tempFolder, true);
                        _logger.LogInformation("Carpeta temporal eliminada completamente");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error eliminando carpeta final");
                    }
                }
                
                _disposed = true;
            }
        }
}