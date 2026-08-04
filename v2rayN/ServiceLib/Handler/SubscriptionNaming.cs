namespace ServiceLib.Handler;

/// <summary>
/// THE ONE ANSWER TO "WHAT IS THIS SUBSCRIPTION CALLED".
///
/// Every surface that prints a subscription's name comes here: the meta card on Главная, the group
/// heading in the server list, the update log/notification, and anything added later. Before this
/// class the ranking was re-invented at each site — and one of those sites, the update log, is
/// produced by a background task, so it simply formatted the RAW remark. With upstream's
/// <c>"import_sub"</c> stored as that remark, the log and the group heading both read
/// «import_sub» in a Russian UI.
///
/// TWO RULES, AND THEY ARE THE OWNER'S:
///
/// 1. <b>A placeholder is not a name.</b> <c>"import_sub"</c>/<c>"import sub"</c> is upstream's
///    English default, <c>"Default"</c> is the linkless local container's, and
///    <c>"departament vpn"</c> is the service label the backend returns on EVERY subscription. None
///    of them identifies anything, so none of them is ever shown — and because <see cref="IsUnnamed"/>
///    is also what the update path asks before adopting the provider's <c>profile-title</c>, an
///    install that already stored one heals itself on its next refresh. Nobody has to reinstall.
/// 2. <b>There is no rename to fall back on.</b> Editing a subscription is not a feature
///    (OWNER-DECISION-2026-08-02 §5), so a bad automatic name would be permanent. The automatic name
///    is the only name, which is exactly why a placeholder must never be allowed to stick.
///
/// <para>Language-neutral on purpose: this file is in <c>ServiceLib</c>, which the upstream WPF
/// client shares, so it holds the RANKING and no user-facing wording. The one word it cannot avoid —
/// what to call a subscription that nothing names yet — comes from
/// <see cref="UntitledNameProvider"/>, which the Departament desktop points at its own live ru/en
/// table; unwired, it stays on upstream's own resource.</para>
/// </summary>
public static class SubscriptionNaming
{
    /// <summary>
    /// Strings that name no subscription, compared case-insensitively against a trimmed candidate.
    ///
    /// <c>import_sub</c>, <c>import sub</c> and <c>Default</c> are the placeholders older builds
    /// STORED, so they are here to heal existing installs, not because anything writes them any more
    /// (the Android client wrote the spaced form, this one the underscored form; both installs of the
    /// product are healed by the same set). <c>departament</c> / <c>departament vpn</c> are the
    /// generic service label — the same string on every subscription of this deployment, which is why
    /// the tariff badge refuses it for the same reason.
    /// </summary>
    public static readonly IReadOnlyCollection<string> Placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "default",
        "import sub",
        "import_sub",
        "departament",
        "departament vpn",
    };

    /// <summary>
    /// Supplies the word for a subscription that nothing names yet. Set by the UI layer that owns the
    /// user's language; when it is null the upstream resource answers, so the WPF client is unaffected.
    /// </summary>
    public static Func<string>? UntitledNameProvider;

    /// <summary><paramref name="candidate"/> when it actually names a subscription, else null.</summary>
    public static string? RealName(string? candidate)
    {
        var trimmed = candidate?.Trim();
        if (trimmed.IsNullOrEmpty())
        {
            return null;
        }
        return Placeholders.Contains(trimmed!) ? null : trimmed;
    }

    /// <summary>
    /// True when nothing on <paramref name="sub"/> names it yet, so the provider's
    /// <c>profile-title</c> may be adopted into its stored remark. This is the healing gate: it is
    /// what turns a stored placeholder back into "unnamed" on the next refresh.
    /// </summary>
    public static bool IsUnnamed(SubItem? sub) => sub is null || RealName(sub.Remarks) is null;

    /// <summary>
    /// The name to print, in the one ranking the whole app uses:
    ///
    /// <list type="number">
    /// <item>the nickname the user set in the cabinet (<paramref name="accountDisplayName"/>),</item>
    /// <item>the provider's own <c>profile-title</c>,</item>
    /// <item>the stored remark,</item>
    /// <item>the backend's per-sub label («Подписка #2», <paramref name="accountDefaultLabel"/>) —
    ///       below the provider's title on purpose, because it is generated rather than chosen,</item>
    /// </list>
    ///
    /// and null when none of them is a real name. The two account arguments are optional: only a
    /// screen holding a live account payload can supply them, and every other caller still gets the
    /// same answer for the same stored data.
    ///
    /// <para>Copy that QUOTES the name needs this rather than <see cref="TitleOf"/> — «Обновляем
    /// «Подписка»» quotes a generic noun back at the user as if it were a title. A caller in that
    /// position picks a different, whole sentence instead.</para>
    /// </summary>
    public static string? NameOf(SubItem? sub, string? accountDisplayName = null, string? accountDefaultLabel = null)
        => RealName(accountDisplayName)
        ?? RealName(sub?.ProfileTitle)
        ?? RealName(sub?.Remarks)
        ?? RealName(accountDefaultLabel);

    /// <summary><see cref="NameOf(SubItem, string, string)"/> for a caller holding the two strings rather than the row.</summary>
    public static string? NameOf(string? profileTitle, string? remarks, string? accountDisplayName = null, string? accountDefaultLabel = null)
        => RealName(accountDisplayName)
        ?? RealName(profileTitle)
        ?? RealName(remarks)
        ?? RealName(accountDefaultLabel);

    /// <summary><see cref="NameOf(SubItem, string, string)"/> with the generic noun as its floor — never empty, never a placeholder.</summary>
    public static string TitleOf(SubItem? sub, string? accountDisplayName = null, string? accountDefaultLabel = null)
        => NameOf(sub, accountDisplayName, accountDefaultLabel) ?? Untitled();

    /// <summary><see cref="NameOf(string, string, string, string)"/> with the generic noun as its floor.</summary>
    public static string TitleOf(string? profileTitle, string? remarks, string? accountDisplayName = null, string? accountDefaultLabel = null)
        => NameOf(profileTitle, remarks, accountDisplayName, accountDefaultLabel) ?? Untitled();

    private static string Untitled()
    {
        var word = UntitledNameProvider?.Invoke();
        return word.IsNotEmpty() ? word! : ResUI.menuSubscription;
    }
}
