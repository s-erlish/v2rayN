namespace ServiceLib.Events;

public static class AppEvents
{
    public static readonly EventChannel<Unit> AddServerViaClipboardRequested = new();
    public static readonly EventChannel<bool> HasUpdateNotified = new();

    public static readonly EventChannel<ServerSpeedItem> DispatcherStatisticsRequested = new();

    public static readonly EventChannel<string> SendSnackMsgRequested = new();
    public static readonly EventChannel<string> SendMsgViewRequested = new();

    public static readonly EventChannel<Unit> AppExitRequested = new();
    public static readonly EventChannel<bool> ShutdownRequested = new();

    // departament (idle/perf B1): the single source of truth for "is the VPN core running?".
    // Raised by CoreManager on EVERY start/stop of the main core (CoreStart → true, CoreStop → false)
    // — the only two places AppManager.RunningCoreType is mutated. Subscribers replace the old
    // 1–2 s busy-pollers (tray label, status-bar tray icon, Home shield) that all existed solely
    // because the engine exposed no connect-state event. Payload = true when a core is now running,
    // false when stopped. Fires on a background thread (CoreManager runs core start inside Task.Run),
    // so UI subscribers must marshal to the UI thread themselves.
    public static readonly EventChannel<bool> CoreRunningStateChanged = new();

    public static readonly EventChannel<ESysProxyType> SysProxyChangeRequested = new();
}
