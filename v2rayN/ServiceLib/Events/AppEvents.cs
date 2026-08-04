namespace ServiceLib.Events;

public static class AppEvents
{
    public static readonly EventChannel<Unit> AddServerViaClipboardRequested = new();
    public static readonly EventChannel<bool> HasUpdateNotified = new();

    public static readonly EventChannel<ServerSpeedItem> DispatcherStatisticsRequested = new();

    public static readonly EventChannel<string> SendSnackMsgRequested = new();
    public static readonly EventChannel<string> SendMsgViewRequested = new();

    /// <summary>
    /// Raised once per "add servers" attempt (clipboard paste, QR scan, screen scan), from EVERY entry
    /// point — the corner «+» flyout, Ctrl+V, the onboarding surface and the tray menu all funnel into
    /// the same method. The payload says what happened and how much of it, and carries no text:
    /// ServiceLib is shared with upstream's WPF client and may not choose the words. The shell that
    /// owns the copy table subscribes and tells the user.
    ///
    /// Publishing is unconditional, including on failure, because "the app said nothing" and "the add
    /// failed" are indistinguishable to a user and were being reported as the same defect.
    /// Fires on whatever thread ran the add — UI subscribers marshal themselves.
    /// </summary>
    public static readonly EventChannel<AddServerOutcome> AddServerOutcomeReported = new();

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

    /// <summary>
    /// Positive "switch settled" signal, raised by <see cref="ServiceLib.Manager.CoreManager.SwitchServer"/>
    /// on ANY successful switch: the Tier 2 hot-swap, the Tier 1 restart-main, AND the full-restart
    /// fallback (including the catch-then-recover path). It lets the UI resolve its mid-switch
    /// "Connecting" hold immediately instead of waiting on the 12s safety deadline. On the seamless
    /// tiers no <see cref="CoreRunningStateChanged"/> is published at all, so this is the only completion
    /// signal there; on the fallback it fires alongside the reload's own CoreRunningStateChanged(true),
    /// which is harmless. Payload is always <c>true</c> (never raised on failure). Fires on a background
    /// thread — same contract as <see cref="CoreRunningStateChanged"/>; UI subscribers marshal to the UI
    /// thread themselves.
    /// </summary>
    public static readonly EventChannel<bool> CoreSwitchSettled = new();

    public static readonly EventChannel<ESysProxyType> SysProxyChangeRequested = new();
}
