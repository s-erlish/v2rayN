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

    /// <summary>
    /// A subscription URL was stored AND its servers arrived. <c>Count</c> carries how many.
    ///
    /// It is reported only after the fetch has finished, never on the strength of the stored row
    /// alone: <c>ConfigHandler.AddBatchServers</c> answers 1 the moment the <c>SubItem</c> is written,
    /// which is true of a subscription that turns out to be unreachable just as much as of one that
    /// works.
    /// </summary>
    SubscriptionAdded,

    /// <summary>Every subscription URL in the pasted data was already stored; it has been refreshed.</summary>
    SubscriptionAlreadyExists,

    /// <summary>
    /// The subscription was stored, but the fetch brought back no servers — unreachable host, a
    /// rejected request, an empty or unparsable body.
    ///
    /// This outcome exists because that failure had NO surface at all. The fetch reports its progress
    /// through <c>NoticeManager.SendMessageEx</c>, which reaches only the message log, and the
    /// Avalonia shell constructs no log view — so an add whose fetch failed still announced
    /// "subscription added, fetching servers" and then went quiet forever, leaving the user on the
    /// first-run screen with nothing to explain it. The shell must offer a retry.
    /// </summary>
    SubscriptionNoServers,

    /// <summary>The attempt threw. The exception is in the log; the user gets a plain failure.</summary>
    Failed,
}
