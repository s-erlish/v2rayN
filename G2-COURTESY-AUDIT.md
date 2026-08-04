# G2 — the courtesy audit of the desktop client

Owner feedback 2026-07-27, section **G2**. He asked for the list *before* the fixing, because the
list is the record:

> «при выборе серверов на андроиде удобно сделано что там предлагает переподключиться, я бы хотел
> чтобы ты такие фишки перенес и на пк 1 в 1 по стилю и дизайну, вот андроид приложение хорошо
> проработано в плане таких мелочей, мне нравится»

Seven rules. Every row below was read off the working tree, not off a previous audit; where an
earlier register row disagrees with what the code says today, the code wins and the row says so.

Paths are relative to `v2rayN/v2rayN.Desktop/` unless stated otherwise.

| # | Rule | Verdict | This pass |
|---|---|---|---|
| 1 | a cancelled action is never reported as a failure | **was failing in one place** | **fixed** |
| 2 | an action that cannot work does not present an enabled control | **was failing** | **fixed** (G1) |
| 3 | an in-flight action shows it, and cannot be fired twice | **holds** | verified, one gap recorded |
| 4 | a destructive action names what it will destroy | **failed for form** | **fixed** |
| 5 | an empty state says what to do next | **failed** | **fixed** |
| 6 | a failure offers the retry, in the same place it reports it | **partly** | **fixed** (the channel existed but had no renderer) |
| 7 | state that changed elsewhere repaints here | **partly** | recorded, not fixed |

---

## 1. A cancelled action is never reported as a failure

**Found:** one real breach, and it was about to get louder. `AddServerViaImageAsync`
(`../ServiceLib/ViewModels/MainWindowViewModel.cs`) handed the file picker's result straight to the
parser. A cancelled picker returns an empty path, which fell into the "nothing recognised" branch —
so closing a file dialog you opened by mistake reported an import failure. It was invisible until
this pass because the message channel had no renderer at all (see rule 6); the moment messages
started rendering it would have become a visible lie.

**Fixed.** The picker's empty result now returns before the parse. Closing the dialog does nothing
and says nothing, which is what closing a dialog means.

**Verified clean elsewhere:** the connect path already distinguishes cancel from failure — the
12-second deadline in `ViewModels/HomeViewModel.cs` (`BeginConnecting`) is armed only for attempts
that actually started, and `ConnectToggle` refuses to start a second attempt at all. The Android
twin has the opposite bug (register M-59) — the desktop does not.

## 2. An action that cannot work does not present an enabled control

**Found:** the headline breach, and it is the owner's own G1 example seen from the other side.
Selecting a server while a tunnel was up **switched the live tunnel immediately** —
`ViewModels/HomeViewModel.cs` `SelectServer` treated "changed default" and "reconnect now" as one
act. There was no way to select without switching: the context menu's «Сделать основным»
(`Views/ServerListView.axaml.cs` `OnRowMakeDefault`) called `SetDefaultServer` directly and did the
same thing, silently. Three controls, one destructive outcome, no offer.

**Fixed — ported from Android one to one** (`MainActivity.setSelectServer` /
`promptApplySelectedServer` / `HomeFragment.applySelectionToRunningTunnel`):

- `ProfilesViewModel.SetDefaultServer(indexId, applyToRunningCore)` — a selection can now be
  persisted without touching a running core. `ApplySelectedServerToRunningCore()` performs the
  switch as a separate, explicit act.
- `HomeViewModel.SelectServer(indexId, applyToRunningTunnel = false)` — while **disconnected** a pick
  still connects (unchanged; this is where the desktop is the better half — register M-61). While
  **connected** it selects only, then raises the offer.
- The offer names the server, in Android's exact words:
  «Выбран {0}. Переподключиться к нему?» · «Переподключиться» (`Common/L.Home.cs`,
  1:1 with `server_selected_reconnect_prompt` / `_generic` / `_action`). Declining leaves the
  connection exactly as it was and keeps the selection for the next connect.
- The flag emoji is stripped from the name inside the sentence
  (`Common/ProfileDisplay.StripLeadingFlag`, port of `FlagUtil.stripLeadingFlag`) — the flag belongs
  to the row, which already draws it.
- «Сделать основным» now routes through the same method, so the visible and the hidden path agree.

**Also fixed in passing:** the server row advertised keyboard focus (`Focusable` / `IsTabStop` +
a focus ring) but Enter and Space did nothing — focus led to a dead end and choosing a server was
mouse-only. `OnRowKeyDown` now activates the row through the same path.

## 3. An in-flight action shows it, and cannot be fired twice

**Holds, and better than the register's last reading.** Verified this pass:

- **Connect** — `HomeViewModel.ConnectToggle` returns early while `IsConnecting || _disconnecting`,
  so an impatient second tap can no longer re-arm the deadline or stack a second `CoreStop`.
  The register's "the connect control has no in-flight lock" is out of date.
- **Checkout** — `ViewModels/BuyViewModel.cs` guards on `IsPaying`.
- **Sub-page push** — idempotent by type in `Views/MainWindow.axaml.cs`.
- **Telegram link** — `TelegramCanLink` goes false for the duration and the pending state shows the
  code, so the control cannot be fired twice.

**Recorded, not fixed:** subscription *update* (`UpdateSubscriptionProcess`) has no in-flight flag —
pressing refresh twice starts two fetches. Harmless (the second overwrites the first) and outside
this list.

## 4. A destructive action names what it will destroy

**Found:** it held for the **text** and failed for the **form**, which is exactly what the owner
reported as D3. `Views/MessageBoxDialog.axaml` asked «Удалить подписку и её серверы?» and then
offered a generic «Подтвердить» in `Button.Primary`, next to a solid tonal «Отмена» — so the
destructive act was the *quieter* of the two controls and read like something already disabled.

**Fixed:**

- The confirm now carries the **verb** — «Удалить», not «Подтвердить» — and the destructive class
  (`Button.Destructive`, solid red, its rest fill declared on the presenter) at every delete site:
  subscription delete (`Views/SubscriptionMetaView.axaml.cs`), server delete
  (`Views/ServerListView.axaml.cs`), and the legacy `ProfilesView` / `SubSettingWindow` windows.
  `Views/RoutingRuleSettingWindow.axaml.cs` carries both a delete and a batch *add* through the same
  channel, so only the delete is painted red — red that appears on an add stops meaning anything.

**One register claim did not survive verification, and it matters because a fix was prescribed from
it.** M-54(b) / M-55 say `Button.Primary` "never declares its rest-state presenter background, so on
pointer-exit the property falls back to a value owned by the Semi theme". Read from Semi.Avalonia
12.1's own source (`Themes/Shared/Button.axaml`), the base `ControlTheme` carries
`^ /template/ ContentPresenter#PART_ContentPresenter → Background = {TemplateBinding Background}` —
the rest state is always the Button's own background, which the archetypes set. Re-declaring it
would have **broken** the one button that legitimately overrides its own fill
(`SupportButton`, `Views/SubscriptionMetaView.axaml`). What the archetypes really failed to override
is the presenter's **`BorderBrush`**, which Semi does repaint on hover and press — that is pinned now.

Also fixed here: the dialog was set in `Font.Grotesk` — the brand face, which contains **zero**
Cyrillic glyphs. Every Russian word in the app's confirm dialog was being drawn by an undeclared OS
fallback. It is `Font.Ui` now.

## 5. An empty state says what to do next

**Found:** `Views/ServerListView.axaml` had icon + title + line and **no action**. The line already
said «Добавьте подписку или отсканируйте QR-код» — advice with nothing to press, so the user has to
go hunting for the control the sentence just described.

**Fixed:** one action, the one the line names — «Добавить из буфера обмена», bound to the command
that already exists. Exactly one, per the 9.5 formula (title · line · action); QR stays on the
connect hero so an empty list does not become a second onboarding screen.

## 6. A failure offers the retry, in the same place it reports the failure

**Found the deeper defect: there was no place.** Both message channels ended in a dead end.

- `NoticeManager.Enqueue` → `AppEvents.SendSnackMsgRequested` → `MainWindow.DelegateSnackMsg`, which
  forwarded to `NoticeManager.SendMessage` →
- `AppEvents.SendMsgViewRequested` → `MsgViewModel` → and **`MsgView` has zero construction sites in
  this shell** (only the view locator and `DesignData`).

So roughly 156 publish sites wrote into the void: add-subscription outcomes, subscription update
progress, validator errors, core lifecycle, account and device failures. The user pressed something,
it worked or it did not, and the app said nothing either way — which is indistinguishable from a
dead button, and is precisely how the owner described the clipboard add.

A second, independent bug in the same channel: the `SendSnackMsgRequested` subscription lived inside
`this.WhenActivated(...)`, so it was **disposed on window deactivation** — the exact mine that had
already been found and defused for the clipboard interaction, because opening the corner «+»
`MenuFlyout` deactivates the window.

**Fixed:**

- A real transient surface. `snackHost` in `Views/MainWindow.axaml` — markup that already existed and
  was deliberately never shown — is now a live toast built from the existing `Border.Toast` class,
  with an optional **action** beside the text. Enter/exit is translate+fade through `Motion.Play`, so
  reduced motion is honoured at play time.
- The surface answers **only explicit user actions and otherwise-silent failures**. It says nothing
  about connection state: the shield on Главная owns that, and that is the chatter the owner
  rejected.
- `Common/Notify.cs` — a desktop-only channel carrying text **plus an action**, because
  `SendSnackMsgRequested` can only carry a string and this rule needs a control. Used for the
  reconnect offer (G1), the add-failure retry, and the Telegram-link failures.
- Both subscriptions moved to the **window's lifetime**, so a flyout can no longer eat the answer.

Retry affordances that already existed and were verified this pass: `Views/DevicesView.axaml`,
`Views/PaymentHistoryView.axaml`, `Views/AccountView.axaml`, `Views/BuyView.axaml`, and the connect
hero's `RetryHint`, which shows the real failure reason when there is one.

## 7. State that changed elsewhere repaints here

**Partly holds — recorded, not fixed.**

What does repaint: the shell's three-way gate (`ApplyShellVisibility`) follows `HomeViewModel.IsEmpty`
and `AccountViewModel.IsLoggedIn`, so adding a subscription replaces onboarding with the real shell
without a revisit; the account tab recomputes on profile refresh; the server list reprojects from
`ProfileItems` on every engine refresh.

What does not: **there is no offline state anywhere outside the account tab.** `Border.OfflineBar`
is defined in `Assets/GlobalStyles.axaml` and has **zero consumers** — with no network, Главная and
Настройки look exactly like they do with one, and only fail at the moment you press something. That
is a real gap and it is bigger than a courtesy; it needs a connectivity source of truth, which does
not exist yet. Recorded here rather than half-built.

---

## Defects found during this audit that are outside G2

Named, not fixed, so they are not lost:

1. **`ServiceLib/ViewModels/StatusBarViewModel.cs`** — `RoutingModeDisplay` assigns the Russian
   literals «Весь трафик · TUN» / «Через системный прокси». `ServiceLib` is shared with upstream's
   WPF client and must stay language-neutral. Currently harmless because the property is bound to
   nothing; it must not reach a view in this state.
2. **`Views/MsgView.axaml` is still unhosted.** The engine's verbose log (subscription fetch
   progress, validator detail) has no surface. Hosting it as-is would drop an AvaloniaEdit geek panel
   with English `ResUI` labels into the Incy shell, so it needs a designed home, not a mount point.
3. **Subscription refresh has no in-flight lock** (rule 3 above).
4. **Space Grotesk ships as a variable font pinned to Light 300** — `fvar` `wght` min 300 /
   **default 300** / max 700, `OS/2 usWeightClass` 300, parsed from
   `Assets/Fonts/SpaceGrotesk.ttf`. Avalonia instantiates the default instance, so every `Bold` role
   in the brand face is **synthesised** by Skia. That is the residue of «шрифт какой-то толстый» on
   the roles that keep the brand face by design (Display, Wordmark, Chip, Numeric). The fix is a
   supply change — vendor static masters (Regular 400 / Medium 500 / Bold 700) the way Golos Text
   already ships, and reference the folder instead of the single file.
