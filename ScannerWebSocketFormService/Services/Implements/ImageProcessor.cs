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