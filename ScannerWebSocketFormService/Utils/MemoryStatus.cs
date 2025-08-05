namespace ScannerWebSocketFormService.Utils;

public class MemoryStatus
{
    public long WorkingSetMB { get; set; }
    public long PrivateMemoryMB { get; set; }
    public long ManagedMemoryMB { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public long TotalMemoryMB { get; set; }
        
    public override string ToString()
    {
        return $"Working: {WorkingSetMB}MB | Private: {PrivateMemoryMB}MB | Managed: {ManagedMemoryMB}MB | GC: {Gen0Collections}/{Gen1Collections}/{Gen2Collections}";
    }
}