using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;  
using PdfSharp.Pdf;
using ScannerWebSocketFormService.Models;
using ScannerWebSocketFormService.Services.Interface; 

namespace ScannerWebSocketFormService.Services.Implements;

public class ImageProcessor : IImageProcessor, IDisposable
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly IWebSocketService _webSocketService;
    private readonly ITempFileManager _tempFileManager;
    private readonly List<Image> _pages = new();
    private bool _disposed = false;
    private bool _scanCancelled = false;
    
    public event EventHandler<ImageProcessedEventArgs>? ImageProcessed;
    public int PageCount => _pages.Count;
    
    public ImageProcessor(
        ILogger<ImageProcessor> logger, 
        IWebSocketService webSocketService,
        ITempFileManager tempFileManager)
    {
        _logger = logger;
        _webSocketService = webSocketService;
        _tempFileManager = tempFileManager;
    }
    
   /* public async Task<bool> ProcessImageFromFileAsync(string filePath)
    {
        try
        {
            // Verificar si el escaneo fue cancelado
            if (_scanCancelled)
            {
                _logger.LogInformation("Procesamiento cancelado por usuario");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "scan_cancelled", 
                    message = "Escaneo cancelado por usuario"
                });
                // Resetear estado en WebSocketService
                await _webSocketService.ForceResetScanningState("Escaneo cancelado por usuario");
                return false;
            }

            Image? img = null;
            var pageNumber = _pages.Count + 1;
            
            _logger.LogInformation("PROCESANDO PÁGINA {PageNumber}", pageNumber);
            await _webSocketService.BroadcastMessageAsync(new {
                type = "page_processing", pageNumber = pageNumber, message = $"Procesando página {pageNumber}"
            });
            
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                try
                {
                    _logger.LogInformation("Archivo recibido: {FilePath}", filePath);
                    _tempFileManager.AddTempFile(filePath);
                    img = Image.FromFile(filePath);
                    _logger.LogInformation("Imagen cargada: {Width}x{Height} px", img.Width, img.Height);
                    
                    // Crear copia y eliminar archivo inmediatamente
                    var imgCopy = new Bitmap(img);
                    img.Dispose();
                    img = imgCopy;
                    
                    try
                    {
                        File.SetAttributes(filePath, FileAttributes.Normal);
                        File.Delete(filePath);
                        _logger.LogInformation("Archivo temporal eliminado inmediatamente: {FileName}", Path.GetFileName(filePath));
                    }
                    catch (Exception delEx)
                    {
                        _logger.LogWarning(delEx, "Error eliminando archivo inmediatamente");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando archivo");
                    img = null;
                }
            }
            else
            {
                _logger.LogError("No se recibió archivo válido del scanner");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "error", message = "No se recibió archivo del scanner"
                });
                return false;
            }
            
            if (img != null)
            {
                _pages.Add(img);
                _logger.LogInformation("PÁGINA {PageNumber} COMPLETADA", _pages.Count);
                
                var memoryUsageMB = GC.GetTotalMemory(false) / (1024 * 1024);
                var tempFiles = Directory.Exists(_tempFileManager.TempFolder) ? 
                    Directory.GetFiles(_tempFileManager.TempFolder).Length : 0;
                
                _logger.LogInformation("Memoria: {MemoryUsageMB} MB | Archivos temp: {TempFiles}", memoryUsageMB, tempFiles);
                
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "page_completed", pageNumber = _pages.Count, totalPages = _pages.Count, memoryUsageMB = memoryUsageMB, tempFiles = tempFiles, message = $"Página {_pages.Count} completada"
                });
                
                // Invocar evento
                ImageProcessed?.Invoke(this, new ImageProcessedEventArgs 
                { 
                    PageNumber = _pages.Count, Success = true, MemoryUsageMB = memoryUsageMB 
                });
                
                // Limpieza periódica
                if (_pages.Count % 1 == 0)
                {
                    _tempFileManager.CleanupTempFiles();
                    GC.Collect();
                    _logger.LogInformation("Limpieza periódica ejecutada");
                }
                return true;
            }
            else
            {
                _logger.LogError("FALLO procesando la página");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "page_error", pageNumber = pageNumber, message = "Error procesando la página"
                });
                ImageProcessed?.Invoke(this, new ImageProcessedEventArgs 
                { 
                    PageNumber = pageNumber, Success = false, ErrorMessage = "Error procesando la página" 
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EXCEPCIÓN procesando imagen");
            await _webSocketService.BroadcastMessageAsync(new {
                type = "error", message = $"Excepción procesando página: {ex.Message}"
            });
            return false;
        }
    }*/
   
   public async Task<bool> ProcessImageFromFileAsync(string filePath)
    {
        try
        {
            // VALIDACIONES DE SEGURIDAD INICIALES
            if (string.IsNullOrWhiteSpace(filePath))
            {
                _logger.LogWarning("Ruta de archivo vacía o nula");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "error", 
                    message = "Ruta de archivo inválida"
                });
                return false;
            }

            // SANITIZAR RUTA PARA LOGS
            var sanitizedPath = SanitizePathForLog(filePath);
            
            // Verificar si el escaneo fue cancelado
            if (_scanCancelled)
            {
                _logger.LogInformation("Procesamiento cancelado por usuario");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "scan_cancelled", 
                    message = "Escaneo cancelado por usuario"
                });
                // Resetear estado en WebSocketService
                await _webSocketService.ForceResetScanningState("Escaneo cancelado por usuario");
                return false;
            }

            Image? img = null;
            var pageNumber = _pages.Count + 1;
            
            _logger.LogInformation("PROCESANDO PÁGINA {PageNumber}", pageNumber);
            await _webSocketService.BroadcastMessageAsync(new {
                type = "page_processing", 
                pageNumber = pageNumber, 
                message = $"Procesando página {pageNumber}"
            });
            
            if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
            {
                try
                {
                    // VALIDACIONES DE SEGURIDAD DEL ARCHIVO
                    var fileInfo = new FileInfo(filePath);
                    
                    // Validar tamaño de archivo (50MB máximo)
                    const long maxFileSize = 50 * 1024 * 1024; // 50MB
                    if (fileInfo.Length > maxFileSize)
                    {
                        _logger.LogWarning("Archivo demasiado grande: {Size} bytes", fileInfo.Length);
                        await _webSocketService.BroadcastMessageAsync(new {
                            type = "error", 
                            message = "Archivo demasiado grande (máximo 50MB)"
                        });
                        return false;
                    }

                    // VALIDAR EXTENSIÓN DE ARCHIVO
                    var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".gif" };
                    var extension = fileInfo.Extension.ToLowerInvariant();
                    
                    if (!allowedExtensions.Contains(extension))
                    {
                        _logger.LogWarning("Extensión de archivo no permitida: {Extension}", extension);
                        await _webSocketService.BroadcastMessageAsync(new {
                            type = "error", 
                            message = $"Tipo de archivo no permitido: {extension}"
                        });
                        return false;
                    }

                    // VALIDAR QUE EL ARCHIVO ESTÉ DENTRO DEL DIRECTORIO TEMPORAL
                    var fullPath = Path.GetFullPath(filePath);
                    var tempPath = Path.GetFullPath(_tempFileManager.TempFolder);
                    
                    if (!fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("Intento de acceso a archivo fuera del directorio temporal: {FilePath}", sanitizedPath);
                        await _webSocketService.BroadcastMessageAsync(new {
                            type = "error", 
                            message = "Acceso a archivo no autorizado"
                        });
                        return false;
                    }

                    _logger.LogInformation("Archivo recibido: {FilePath}", sanitizedPath);
                    
                    // TIMEOUT PARA OPERACIONES DE ARCHIVO
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    
                    // Agregar archivo a gestión temporal con validación
                    _tempFileManager.AddTempFile(filePath);
                    
                    // CARGAR IMAGEN CON TIMEOUT
                    var loadImageTask = Task.Run(() =>
                    {
                        cts.Token.ThrowIfCancellationRequested();
                        return Image.FromFile(filePath);
                    }, cts.Token);

                    img = await loadImageTask;
                    
                    if (img == null)
                    {
                        _logger.LogError("La imagen cargada es nula");
                        await _webSocketService.BroadcastMessageAsync(new {
                            type = "error", 
                            message = "Error cargando imagen"
                        });
                        return false;
                    }

                    // VALIDAR DIMENSIONES DE IMAGEN (prevenir imágenes excesivamente grandes)
                    const int maxDimension = 10000; // 10000 pixels máximo por dimensión
                    if (img.Width > maxDimension || img.Height > maxDimension)
                    {
                        _logger.LogWarning("Imagen demasiado grande: {Width}x{Height} px", img.Width, img.Height);
                        img.Dispose();
                        await _webSocketService.BroadcastMessageAsync(new {
                            type = "error", 
                            message = $"Imagen demasiado grande (máximo {maxDimension}x{maxDimension} pixels)"
                        });
                        return false;
                    }

                    _logger.LogInformation("Imagen cargada: {Width}x{Height} px", img.Width, img.Height);
                    
                    // Crear copia y eliminar archivo inmediatamente
                    var imgCopy = new Bitmap(img);
                    img.Dispose();
                    img = imgCopy;
                    
                    // ELIMINAR ARCHIVO CON MANEJO SEGURO DE ERRORES
                    await SafeDeleteFileAsync(filePath);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Timeout procesando archivo: {FilePath}", sanitizedPath);
                    img?.Dispose();
                    await _webSocketService.BroadcastMessageAsync(new {
                        type = "error", 
                        message = "Timeout procesando archivo"
                    });
                    return false;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError(ex, "Acceso no autorizado al archivo: {FilePath}", sanitizedPath);
                    img?.Dispose();
                    await _webSocketService.BroadcastMessageAsync(new {
                        type = "error", 
                        message = "Acceso no autorizado al archivo"
                    });
                    return false;
                }
                catch (OutOfMemoryException ex)
                {
                    _logger.LogError(ex, "Memoria insuficiente procesando archivo: {FilePath}", sanitizedPath);
                    img?.Dispose();
                    await _webSocketService.BroadcastMessageAsync(new {
                        type = "error", 
                        message = "Memoria insuficiente para procesar la imagen"
                    });
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error procesando archivo: {FilePath}", sanitizedPath);
                    img?.Dispose();
                    await _webSocketService.BroadcastMessageAsync(new {
                        type = "error", 
                        message = "Error procesando archivo de imagen"
                    });
                    return false;
                }
            }
            else
            {
                _logger.LogError("No se recibió archivo válido del scanner o el archivo no existe");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "error", 
                    message = "No se recibió archivo del scanner"
                });
                return false;
            }
            
            if (img != null)
            {
                _pages.Add(img);
                _logger.LogInformation("PÁGINA {PageNumber} COMPLETADA", _pages.Count);
                
                var memoryUsageMB = GC.GetTotalMemory(false) / (1024 * 1024);
                var tempFiles = Directory.Exists(_tempFileManager.TempFolder) ? 
                    Directory.GetFiles(_tempFileManager.TempFolder).Length : 0;
                
                _logger.LogInformation("Memoria: {MemoryUsageMB} MB | Archivos temp: {TempFiles}", memoryUsageMB, tempFiles);
                
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "page_completed", 
                    pageNumber = _pages.Count, 
                    totalPages = _pages.Count, 
                    memoryUsageMB = memoryUsageMB, 
                    tempFiles = tempFiles, 
                    message = $"Página {_pages.Count} completada"
                });
                
                // Invocar evento
                ImageProcessed?.Invoke(this, new ImageProcessedEventArgs 
                { 
                    PageNumber = _pages.Count, 
                    Success = true, 
                    MemoryUsageMB = memoryUsageMB 
                });
                
                // LIMPIEZA PERIÓDICA MEJORADA
                if (_pages.Count % 1 == 0)
                {
                    _tempFileManager.CleanupTempFiles();
                    
                    // VERIFICAR USO DE MEMORIA ANTES DE GC
                    var memoryAfterPage = GC.GetTotalMemory(false) / (1024 * 1024);
                    if (memoryAfterPage > 100) // Si usa más de 100MB, limpiar más agresivamente
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        _logger.LogInformation("Limpieza agresiva de memoria ejecutada");
                    }
                    else
                    {
                        GC.Collect();
                        _logger.LogInformation("Limpieza periódica ejecutada");
                    }
                }
                return true;
            }
            else
            {
                _logger.LogError("FALLO procesando la página");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "page_error", 
                    pageNumber = pageNumber, 
                    message = "Error procesando la página"
                });
                ImageProcessed?.Invoke(this, new ImageProcessedEventArgs 
                { 
                    PageNumber = pageNumber, 
                    Success = false, 
                    ErrorMessage = "Error procesando la página" 
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "EXCEPCIÓN procesando imagen");
            await _webSocketService.BroadcastMessageAsync(new {
                type = "error", 
                message = "Error interno procesando página"
            });
            return false;
        }
    }

    // MÉTODOS AUXILIARES DE SEGURIDAD 

    /// <summary>
    /// Sanitiza la ruta del archivo para logs, removiendo información sensible
    /// </summary>
    private string SanitizePathForLog(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return "null";
        
        try
        {
            var fileName = Path.GetFileName(filePath);
            var directory = Path.GetDirectoryName(filePath);
            
            // Solo mostrar el nombre del archivo y la carpeta temporal
            if (directory != null && directory.Contains("BrotherScan_"))
            {
                var tempFolderName = Path.GetFileName(directory);
                return $"{tempFolderName}\\{fileName}";
            }
            
            return fileName ?? "unknown";
        }
        catch
        {
            return "sanitized_path";
        }
    }

    /// <summary>
    /// Elimina un archivo de manera segura con reintentos
    /// </summary>
    private async Task SafeDeleteFileAsync(string filePath)
    {
        const int maxRetries = 3;
        const int delayMs = 100;
        
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.SetAttributes(filePath, FileAttributes.Normal);
                    File.Delete(filePath);
                    _logger.LogInformation("Archivo temporal eliminado: {FileName}", Path.GetFileName(filePath));
                    return;
                }
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogDebug("Error eliminando archivo (intento {Attempt}): {Error}", attempt, ex.Message);
                await Task.Delay(delayMs * attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error final eliminando archivo después de {MaxRetries} intentos", maxRetries);
                break;
            }
        }
    }
    
    public async Task SendPagesViaWebSocketAsync()
    {
        try
        {
            if (_pages.Count == 0)
            {
                _logger.LogInformation("No hay páginas para enviar");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "scan_completed", totalPages = 0, message = "Escaneo completado sin páginas"
                });
                return;
            }
            
            // LLAMADA AL METODO PARA GENERAR PDF EN MEMORIA Y ENVIARLO POR WEBSOCKET
            await GenerateAndSendPdfViaWebSocket();
            
            _logger.LogInformation("PDF enviado via WebSocket: {Count} páginas", _pages.Count);
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "scan_session_ended", message = "Scanner listo para nuevo escaneo"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando PDF via WebSocket");
        }
    }
    
    // Generar PDF en memoria y enviar por WebSocket
    private async Task GenerateAndSendPdfViaWebSocket()
    {
        try
        {
            using var doc = new PdfDocument();
            
            foreach (var img in _pages)
            {
                var page = doc.AddPage();
                
                const double defaultDpi = 300.0;
                double widthInches = img.Width / defaultDpi;
                double heightInches = img.Height / defaultDpi;
                
                page.Width = XUnit.FromPoint(widthInches * 72);
                page.Height = XUnit.FromPoint(heightInches * 72);
                using var gfx = XGraphics.FromPdfPage(page);
                using var ms = new MemoryStream();
                
                var format = img.PixelFormat.ToString().Contains("Indexed") ? 
                    System.Drawing.Imaging.ImageFormat.Png : 
                    System.Drawing.Imaging.ImageFormat.Jpeg;
                
                if (format == System.Drawing.Imaging.ImageFormat.Jpeg)
                {
                    var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                        .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                    var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, 95L);
                    img.Save(ms, jpegEncoder, encoderParams);
                }
                else
                {
                    img.Save(ms, format);
                }
                
                ms.Position = 0;
                using var ximg = XImage.FromStream(ms);
                gfx.DrawImage(ximg, 0, 0, page.Width.Point, page.Height.Point);
            }
            
            // GUARDAR PDF EN MEMORIA Y ENVIARLO POR WEBSOCKET
            using var pdfStream = new MemoryStream();
            doc.Save(pdfStream);
            var pdfBytes = pdfStream.ToArray();
            var base64Pdf = Convert.ToBase64String(pdfBytes);
            var fileName = $"escaneo_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            
            var pdfData = new {
                type = "pdf_completed", fileName = fileName, base64Pdf = base64Pdf, fileSize = pdfBytes.Length, totalPages = _pages.Count, message = $"PDF completado con {_pages.Count} páginas"
            };
            await _webSocketService.BroadcastMessageAsync(pdfData);
            
            _logger.LogInformation("PDF final enviado via WebSocket: {FileName} ({Size} KB)", fileName, pdfBytes.Length / 1024);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generando PDF");
        }
    }
    
    public async Task CancelScanAsync()
    {
        _scanCancelled = true;
        _logger.LogInformation("Escaneo cancelado por usuario");
        
        // Notificar inmediatamente al WebSocketService
        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_cancelled", 
            message = "Escaneo cancelado por usuario"
        });
        
        // Resetear estado en WebSocketService
        await _webSocketService.ForceResetScanningState("Escaneo cancelado por usuario");
    }
    
    public void ResetCancelFlag()
    {
        _scanCancelled = false;
    }
    
    public void ClearPages()
    {
        try
        {
            foreach (var page in _pages)
            {
                page?.Dispose();
            }
            _pages.Clear();
            _logger.LogInformation("Páginas limpiadas");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error limpiando páginas");
        }
    }
    
    public void Dispose()
    {
        if (!_disposed)
        {
            ClearPages();
            _disposed = true;
        }
    }
}