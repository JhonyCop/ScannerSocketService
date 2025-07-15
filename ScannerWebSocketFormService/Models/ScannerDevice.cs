namespace ScannerWebSocketFormService.Models;

public class ScannerDevice
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ScannerType Type { get; set; }
    public object? NativeDevice { get; set; }
    public bool IsAvailable { get; set; } = true;
    
    public override string ToString()
    {
        return $"[{Type}] {Name}";
    }
}