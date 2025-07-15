namespace ScannerWebSocketFormService.Models;

public class SystemStateInfo
{
    public bool IsSystemSuspended { get; set; }
    public bool IsSessionLocked { get; set; }
    public DateTime? SuspendTime { get; set; }
    public DateTime? ResumeTime { get; set; }
    public TimeSpan? SuspendDuration { get; set; }
    public int SuspendHandlerCount { get; set; }
    public int ResumeHandlerCount { get; set; }
    public int LockHandlerCount { get; set; }
    public int UnlockHandlerCount { get; set; }

    public override string ToString()
    {
        return $"Suspended: {IsSystemSuspended}, Locked: {IsSessionLocked}, " +
               $"Duration: {SuspendDuration?.ToString(@"hh\:mm\:ss") ?? "N/A"}, " +
               $"Handlers: S={SuspendHandlerCount}, R={ResumeHandlerCount}, " +
               $"L={LockHandlerCount}, U={UnlockHandlerCount}";
    }
}