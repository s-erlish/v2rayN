namespace ServiceLib.Enums;

/// <summary>
/// Language-neutral outcome of an "add servers" attempt (clipboard paste, QR scan, screen scan).
///
/// It exists because the outcome has to be TOLD to the user and ServiceLib is shared with upstream's
/// WPF client: it must stay UI-free and language-neutral, so it may not choose the words. The engine
/// reports WHAT happened through this enum on <c>AppEvents.AddServerOutcomeReported</c>; the shell
/// that owns the copy table turns it into a sentence and shows it. Before this channel existed the
/// subscription branch wrote only to the log sink, which has no renderer in the Avalonia shell — so
/// pasting a subscription link produced no visible reaction at all, success or failure alike.
/// </summary>
public enum EAddOutcome
{
    /// <summary>The clipboard was empty or could not be read.</summary>
    ClipboardEmpty,

    /// <summary>Data was present but nothing in it parsed as a server link or a subscription URL.</summary>
    NothingRecognised,

    /// <summary>One or more server links were imported. <c>Count</c> carries how many.</summary>
    ServersImported,

    /// <summary>A subscription URL was stored and its servers are being fetched.</summary>
    SubscriptionAdded,

    /// <summary>Every subscription URL in the pasted data was already stored; it is being refreshed.</summary>
    SubscriptionAlreadyExists,

    /// <summary>The attempt threw. The exception is in the log; the user gets a plain failure.</summary>
    Failed,
}
