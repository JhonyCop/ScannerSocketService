using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;  
using PdfSharp.Pdf;
using ScannerWebSocketFormService.Models;
using ScannerWebSocketFormService.Services.Interface;
using Timer = System.Threading.Timer;

namespace ScannerWebSocketFormService.Services.Implements;

public class ImageProcessor : IImageProcessor, IDisposable
{
    private readonly ILogger<ImageProcessor> _logger;
    private readonly IWebSocketService _webSocketService;
    private readonly ITempFileManager _tempFileManager;
    private readonly List<Image> _pages = new();
    private bool _disposed = false;
    private bool _scanCancelled = false;
    private readonly Timer _periodicCleanupTimer;
    
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

        _periodicCleanupTimer = new Timer(_ =>
        {
            try
            {
                _tempFileManager.CleanupTempFiles();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                _logger.LogInformation("Limpieza periódica automática ejecutada ");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error durante la limpieza periódica automática");
            }
        }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        
    }
    
    public async Task<bool> ProcessImageFromFileAsync(string filePath)
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
                    
                    LogMemoryStatus("Antes de cargar imagen");

                    // Crear imagen y copia optimizada
                    using (var originalImg = Image.FromFile(filePath))
                    {
                        _logger.LogInformation("Imagen cargada: {Width}x{Height} px", originalImg.Width, originalImg.Height);
                        
                        // Crear copia optimizada de la imagen
                        img = new Bitmap(originalImg);
                    } // La imagen original se libera automáticamente aquí
                    
                    // Eliminar archivo inmediatamente después de cargar
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
                    img?.Dispose(); // Asegurar liberación en caso de error
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
                LogMemoryStatus("Después de agregar imagen");
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
                
                // Limpieza periódica más agresiva
                if (_pages.Count % 1 == 0)
                {
                    _tempFileManager.CleanupTempFiles();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    _logger.LogInformation("Limpieza periódica ejecutada");
                    
                    // Log de memoria después de limpieza
                    var memoryAfterCleanup = GC.GetTotalMemory(true) / (1024 * 1024);
                    _logger.LogInformation("Memoria después de limpieza: {MemoryUsageMB} MB", memoryAfterCleanup);
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
    }
    
    public async Task SendPagesViaWebSocketAsync()
    {
        try
        {
            LogMemoryStatus("Antes de enviar PDF");

            
            if (_pages.Count == 0)
            {
                _logger.LogInformation("No hay páginas para enviar");
                await _webSocketService.BroadcastMessageAsync(new {
                    type = "scan_completed", totalPages = 0, message = "Escaneo completado sin páginas"
                });
                return;
            }
            
            var pageCount = _pages.Count;
            _logger.LogInformation("Iniciando generación de PDF con {PageCount} páginas", pageCount);
            
            // LLAMADA AL METODO PARA GENERAR PDF EN MEMORIA Y ENVIARLO POR WEBSOCKET
            
            await GenerateAndSendPdfViaWebSocket();
            
            
            // LIMPIAR TODAS LAS PÁGINAS DESPUÉS DE GENERAR EL PDF
            ClearPages();
            
            // Forzar recolección de basura después de limpiar páginas
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            
            var memoryAfterSend = GC.GetTotalMemory(true) / (1024 * 1024);
            _logger.LogInformation("PDF enviado y memoria liberada. Memoria actual: {MemoryUsageMB} MB", memoryAfterSend);
            
            await _webSocketService.BroadcastMessageAsync(new {
                type = "scan_session_ended", 
                message = "Scanner listo para nuevo escaneo",
                memoryCleared = true,
                memoryUsageMB = memoryAfterSend
            });
            
            LogMemoryStatus("Después de enviar PDF y limpiar");

            
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error enviando PDF via WebSocket");
            // Asegurar limpieza incluso en caso de error
            ClearPages();
            GC.Collect();
        }
    }
    
    // Generar PDF en memoria y enviar por WebSocket - OPTIMIZADO
    private async Task GenerateAndSendPdfViaWebSocket()
    {
        PdfDocument? doc = null;
        byte[]? pdfBytes = null;
        string? base64Pdf = null;

        
        try
        {
            doc = new PdfDocument();
            
            for (int i = 0; i < _pages.Count; i++)
            {
                var img = _pages[i];
                if (img == null) continue;
                
                _logger.LogInformation("Procesando página {PageNumber} de {TotalPages} para PDF", i + 1, _pages.Count);
                
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
                
                // Optimizar compresión de imagen
                if (format == System.Drawing.Imaging.ImageFormat.Jpeg)
                {
                    var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                        .First(c => c.FormatID == System.Drawing.Imaging.ImageFormat.Jpeg.Guid);
                    using var encoderParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encoderParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, 90L); // Reducir calidad ligeramente para ahorrar memoria
                    img.Save(ms, jpegEncoder, encoderParams);
                }
                else
                {
                    img.Save(ms, format);
                }
                
                ms.Position = 0;
                using var ximg = XImage.FromStream(ms);
                gfx.DrawImage(ximg, 0, 0, page.Width.Point, page.Height.Point);
                
                // Limpiar MemoryStream explícitamente
                ms.SetLength(0);
                
                // Liberar la imagen procesada inmediatamente después de agregarla al PDF
                img.Dispose();
                _pages[i] = null!; // Marcar como null para evitar referencia
                
                // Recolección periódica durante la generación del PDF
                if ((i + 1) % 5 == 0) // Cada 5 páginas
                {
                    GC.Collect();
                    var currentMemory = GC.GetTotalMemory(false) / (1024 * 1024);
                    _logger.LogInformation("Memoria durante generación PDF (página {Page}): {Memory} MB", i + 1, currentMemory);
                }
            }
            
            // GUARDAR PDF EN MEMORIA
            using var pdfStream = new MemoryStream();
            doc.Save(pdfStream);
            pdfBytes = pdfStream.ToArray();
            
            // Liberar el documento PDF inmediatamente después de obtener los bytes
            doc?.Dispose();
            doc = null;
            
            // Forzar recolección antes de enviar
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            
            var fileName = $"escaneo_{DateTime.Now:yyyyMMdd_HHmmss}.pdf";
            var fileSizeKB = pdfBytes.Length / 1024;
            
            _logger.LogInformation("PDF generado en memoria: {FileName} ({Size} KB)", fileName, fileSizeKB);
            
            await _webSocketService.BroadcastBinaryAsyncTwo(pdfBytes, "application/pdf", fileName);
            
           Array.Clear(pdfBytes, 0, pdfBytes.Length);
           pdfBytes = null;
           GC.Collect();
           GC.WaitForPendingFinalizers();
           GC.Collect();
           _pages.Clear();
           ImageProcessed = null;

            
            _logger.LogInformation("PDF enviado via WebSocket: {FileName} ({Size} KB)", fileName, fileSizeKB);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generando PDF");
            throw; // Re-lanzar para que el método padre maneje la limpieza
        }
        finally
        {
            // LIMPIEZA GARANTIZADA
            doc?.Dispose();
            
            // Limpiar referencia a bytes del PDF
            if (pdfBytes != null)
            {
                Array.Clear(pdfBytes, 0, pdfBytes.Length);
                pdfBytes = null;
            }
            
            base64Pdf = null;
            doc = null;
            
            // Forzar recolección final
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            _pages.Clear();
            ImageProcessed = null;
            
            var finalMemory = GC.GetTotalMemory(true) / (1024 * 1024);
            _logger.LogInformation("Memoria después de generar PDF: {Memory} MB", finalMemory);
        }
    }
    
    public async Task CancelScanAsync()
    {
        _scanCancelled = true;
        _logger.LogInformation("Escaneo cancelado por usuario");
        
        // Limpiar páginas cuando se cancela
        ClearPages();
        
        // Notificar inmediatamente al WebSocketService
        await _webSocketService.BroadcastMessageAsync(new {
            type = "scan_cancelled", 
            message = "Escaneo cancelado por usuario"
        });
        
        // Resetear estado en WebSocketService
        await _webSocketService.ForceResetScanningState("Escaneo cancelado por usuario");
        
        // Forzar limpieza de memoria
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
    
    public void ResetCancelFlag()
    {
        _scanCancelled = false;
    }
    
    public void ClearPages()
    {
        LogMemoryStatus("Antes de ClearPages()");

        try
        {
            var pageCount = _pages.Count;
            foreach (var page in _pages)
            {
                page?.Dispose();
            }
            _pages.Clear();
            
            // Forzar recolección después de limpiar páginas
            if (pageCount > 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }
            LogMemoryStatus("Después de ClearPages()");

            
            _logger.LogInformation("Páginas limpiadas: {PageCount} páginas liberadas", pageCount);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error limpiando páginas");
        }
    }
    
    private void LogMemoryStatus(string context)
    {
        var totalMemory = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024.0); // .NET heap
        var processMemory = Process.GetCurrentProcess().PrivateMemorySize64 / (1024.0 * 1024.0); // Total usado por el proceso

        _logger.LogInformation("🔍 [{Context}] Memoria .NET: {ManagedMemory:F2} MB | Memoria total del proceso: {ProcessMemory:F2} MB | Imágenes cargadas: {_pages.Count}",
            context, totalMemory, processMemory, _pages.Count);
    }

    
    public void Dispose()
    {
        LogMemoryStatus("Dispose() - inicio");

        if (!_disposed)
        {
            ClearPages();
            _disposed = true;
            _periodicCleanupTimer?.Dispose();
            
            
            // Limpieza final
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            LogMemoryStatus("Dispose() - fin");

            
        }
    }
}