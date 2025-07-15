namespace ScannerWebSocketFormService.Services.Interface;

public interface ITempFileManager
{
    string TempFolder { get; }
        
    void Initialize();
    void AddTempFile(string filePath);
    void CleanupTempFiles();
    void CleanupAll();
    void Dispose();
}