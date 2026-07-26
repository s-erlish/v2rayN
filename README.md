# Departament VPN — desktop client

This repository builds the desktop client of Departament VPN for Windows, Linux and macOS. It is a
VPN/proxy client that supervises an external core process (Xray, sing-box and the other cores
upstream supports) and takes its servers from the user's Departament subscription.

It is one half of a two-client product. The Android half lives in a separate repository — see
[The Android client](#the-android-client).

The app still works as a plain proxy client with no account: you can paste a share link, import a
subscription URL and connect. Signing in is what adds the Departament side of the product —
subscriptions, tariffs, devices, payments, referrals.

## Relationship to upstream

This is a fork of [2dust/v2rayN](https://github.com/2dust/v2rayN), GPL-3.0. Everything that speaks
the protocols is upstream's work and is kept as-is: core process supervision, core config
generation, share-link and subscription parsing, routing, DNS, TUN, system proxy, statistics,
speedtest, WebDAV backup. All of that lives in `ServiceLib`.

What this fork adds sits on top of it:

- **The account layer** — `v2rayN.Desktop/Account/**`, a port of the Android client's `auth/**`
  against the same backend.
- **The account tab and its billing surfaces** — `AccountView`, and the sub-pages `BuyView`
  (tariffs and checkout), `DevicesView` (per-subscription HWID devices), `PaymentHistoryView`.
- **Sign-in and onboarding** — `LoginView`, `OnboardingView`, `AccountSyncView`.
- **The Incy design layer** — the token set, the class-style vocabulary, the motion rules and the
  Russian copy table described under [Design law](#design-law).
- **Product identity** — the Avalonia host builds as `departament`
  (`AssemblyName`, `Product`, `AssemblyTitle`, `Company` in `v2rayN.Desktop.csproj`), so it also
  identifies as `departament` in Task Manager rather than as v2rayN.

Namespaces, the solution name and the `v2rayN*` project names are deliberately unchanged. File
paths still match upstream, so merges and diffs against it stay reviewable.

## Repository layout

Everything builds from `v2rayN/`.

| Path | What it is |
|---|---|
| `v2rayN/v2rayN.Desktop` | **The Departament client.** Avalonia, cross-platform, `net10.0`. `Views/**` (AXAML + code-behind), `ViewModels/**`, `Account/**`, `Assets/**` (tokens, styles, fonts, flags, tray icons), `Common/**` (localization, motion, UI-scale, view locator), `Manager/**`, `Base/**`. |
| `v2rayN/ServiceLib` | The shared, UI-free core. Profiles and storage (`Helper/SqliteHelper.cs`), core config generation (`Services/CoreConfig/**`), link and subscription format parsing (`Handler/Fmt/**`), core supervision (`Manager/**`), models, events, upstream's `Resx/ResUI` string resources, and the upstream view models both UI projects share. |
| `v2rayN/v2rayN` | Upstream's WPF client, Windows only. Still in the solution; **not** what this fork ships. |
| `v2rayN/AmazTool` | Small console helper published beside the app; restarts it and applies a downloaded upgrade. |
| `v2rayN/GlobalHotKeys` | Git submodule — see [Build](#build). |
| `v2rayN/ServiceLib.Tests` | xUnit tests over core config generation (`CoreConfig/`) and link/subscription parsing (`Fmt/`). |
| `v2rayN/ServiceLib.UdpTest` | UDP probe helper used by the speedtest path. |
| `package-*.sh` (repo root) | Upstream's deb/rpm/macOS packaging. Inherited unmodified; see [Release readiness](#release-readiness) before using any of them. |

The split matters when deciding where a change belongs: **anything with a pixel in it goes in
`v2rayN.Desktop`; anything the tunnel needs goes in `ServiceLib`.** `ServiceLib` is shared with the
WPF project and must stay UI-free and language-neutral — hard-coding a Russian string there leaks it
into the English UI.

## Stack

C# on .NET 10 (`TargetFramework net10.0`, pinned in `v2rayN/Directory.Build.props`), Avalonia 12.1
with Semi.Avalonia, DialogHost.Avalonia and AvaloniaEdit, and ReactiveUI (with Fody) for view models
and commands.

- **Compiled bindings are on by default** (`AvaloniaUseCompiledBindingsByDefault`), so a binding
  typo is a build error rather than a silent blank.
- **Package versions are pinned centrally** in `v2rayN/Directory.Packages.props`
  (`ManagePackageVersionsCentrally`). Do not put a `Version` on a `PackageReference`; version
  overrides are disabled.
- Release builds are single-file with embedded symbols. On Windows, `app.manifest` currently
  requests elevation so TUN mode can create the wintun adapter.

## Build

**A clean checkout must initialise the submodule.** `.gitmodules` declares one:
`v2rayN/GlobalHotKeys` ← `https://github.com/2dust/GlobalHotKeys`. `v2rayN.Desktop.csproj` has an
unconditional `ProjectReference` to it, so a non-recursive clone fails during restore with
`MSB3202: The project file … was not found` — before any compiler runs, and with nothing in the
message to say a submodule is involved. Clone with `--recurse-submodules`, or:

```bash
git submodule update --init --recursive
```

CI does this on every workflow that builds. The library itself is pure Win32 and is only ever
constructed on Windows; Linux and macOS builds link it and never call it.

Then:

```bash
export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH
cd v2rayN

dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Release
dotnet test ./ServiceLib.Tests
```

Always name the `.csproj` explicitly. Both `v2rayN.sln` and `v2rayN.slnx` exist side by side, so
`dotnet build` with no argument fails with `MSB1011` (more than one solution file).

To run a local Debug build:

```bash
cd v2rayN
dotnet run --project v2rayN.Desktop/v2rayN.Desktop.csproj
```

The host executable is named `departament` (`departament.exe` on Windows), not `v2rayN`.

### Publish

```bash
cd v2rayN
dotnet publish ./v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r linux-x64 -p:SelfContained=true -o ../dist
dotnet publish ./AmazTool/AmazTool.csproj -c Release -r linux-x64 -p:SelfContained=true -o ../dist
```

Swap the RID for `win-x64`, `win-arm64`, `linux-arm64`, `osx-x64` or `osx-arm64`; Windows targets
additionally pass `-p:EnableWindowsTargeting=true`. `-r` is not optional: `PublishSingleFile` is set
globally for Release in `Directory.Build.props`, and a publish without a RuntimeIdentifier fails with
`NETSDK1098`.

`.github/workflows/departament-branch-build.yml` is the departament-branded workflow; it publishes
self-contained win-x64, verifies the output is `departament.exe`, and bundles the cores.

### The cores are not in this repository

The app launches an external core from a per-core directory under `bin/` next to the executable
(`Utils.GetBinPath`, which lower-cases the core type — `bin/xray/`, `bin/sing_box/`) and reads
`bin/geoip.dat` and `bin/geosite.dat` from `bin/` itself. `departament-branch-build.yml` downloads
the cores and geo files into the published `bin/`; otherwise the app's own update check fetches them
(`ServiceLib/Services/UpdateService.cs`). A build with an empty `bin/` starts, but there is no core
for it to run.

## Verifying a change

The environment setup and the build gate for **both** clients live in the Android repository,
because one gate covers the product. In this environment the Android repo is `/home/user/dp`:

```bash
bash /home/user/dp/docs/agents/setup-env.sh             # once per container, idempotent
bash /home/user/dp/docs/agents/verify-build.sh desktop   # or: android | both
```

The bar is both of these, together:

- **`BUILD: SUCCESSFUL`** — no errors.
- **`NEW WARNINGS: 0`** — the gate normalises each warning (strips paths and `line:col`, which move
  when unrelated code above them changes) and diffs the result against
  `docs/agents/.baseline-warnings-desktop.txt`. The desktop baseline is 28 warnings. Fewer is
  welcome; new ones fail.

Two things the gate does that are easy to miss. It prints whether the compiler actually ran — an
up-to-date build emits no warnings and proves nothing, so a green line on a no-op build is not
verification. And it serialises builds behind a lock, so it can sit waiting a while before starting
if another agent holds it.

**Avalonia compiles AXAML during the build.** Malformed markup, an unknown control, a missing
resource key and — because compiled bindings are on — a mistyped binding path are all *build
failures*, not runtime surprises. There is no excuse for committing broken markup: the build would
have told you.

`dotnet test ./ServiceLib.Tests` covers core config generation and link parsing. It does not cover
any view, view model or account code.

## The Android client

The Android client is a separate repository, a fork of
[2dust/v2rayNG](https://github.com/2dust/v2rayNG). In this environment it is `/home/user/dp`.

The two clients are **one product under one design**, not two apps that happen to share a backend:

- They talk to the same Departament backend. This repo's `Account/**` is a deliberate port of the
  Android `auth/**` — same endpoint contract, same session rules, same DTO shapes. Endpoint
  semantics are shared with the web dashboard and the Telegram mini app, so they are not ours to
  change unilaterally.
- **The design specifications live in the Android repository, under `docs/design2026/`**, and they
  cover both clients side by side. There is no separate desktop spec repo. In this environment:
  `/home/user/dp/docs/design2026/`.
- The parity contract is written down — `00-rules.md` section 13. **Identical across platforms:** the
  destination set and its order, every user-visible Russian string for the same concept, the default
  value of every setting, the group order inside settings, the state matrix, the token values, the
  motion tempo. **Allowed to differ:** navigation shape (bottom bar vs left rail), per-item action
  surface (bottom sheet vs flyout), hover (desktop only), haptics (Android only), keyboard shortcuts
  (desktop only), window chrome. A feature on one client and not the other is a parity gap logged in
  that platform's spec file, not a platform difference to shrug at.

## Design law

All UI work follows `docs/design2026/` in the Android repository, starting with **`00-rules.md`**,
which outranks taste, habit and upstream precedent. It defines the Incy visual language — pure dark
surface, one bright blue accent, red for destructive only — plus the tokens (one spacing scale, one
radius ramp, one type ramp), the absolute bans, the required states, and the register of every
user-visible string.

Then `03-direction.md` for the visual direction, `10-design-system.md` for the component vocabulary,
`11-app-structure.md` for navigation and routes, and `33-master-plan-pc.md` for this client screen by
screen.

Section 12 is the desktop translation, and these are the mechanics that keep biting:

- **`{DynamicResource Brush.*}` only.** `StaticResource` on a theme-dependent brush freezes the value
  at load and breaks live theme switching and the mono overlay. Brushes and tokens live in
  `Assets/GlobalResources.axaml`; component styles are class selectors in `Assets/GlobalStyles.axaml`
  (`Border.Card`, `Border.Row`, `Button.NavRailItem`, …). A view that hand-rolls a card is a defect —
  extend the class.
- **Keyboard-complete screens.** Every focusable control shows a visible focus ring, tab order follows
  visual order, and nothing is reachable only by mouse. `:pointerover` is a real designed state here.
- **Usable at 900x600**, content capped and centred in wider windows, one scroll region per view — no
  nested scrollers.
- **Reduced motion is read at play time.** `Common/MotionState.cs` is the single live source of truth;
  a view that reads the setting once in its constructor is the exact bug that file exists to fix.
- **No default Fluent or Semi styling may leak through.**

Departament copy goes through `Common/L.*.cs` — a live ru/en table with a `{loc:T Key}` markup
extension and no-restart language switching, split into per-area partials (`L.Common`, `L.Home`,
`L.Servers`, `L.Settings`, `L.Account`, `L.Buy`, `L.Shell`). Upstream strings still come from
`ServiceLib/Resx/ResUI`. Golos Text (`Assets/Fonts/`) is the UI face because it carries Cyrillic;
Space Grotesk is the brand face and is scoped to display, chip, numeric and wordmark roles only.

Read the rules before designing, not after.

## Navigation: three destinations, and no Серверы tab

**Owner decision, 2026-07-26: the desktop must not gain a Серверы destination.** It is recorded in
`docs/design2026/11-app-structure.md` §2.0 and mirrored in the doc comment on `AppTab`
(`Views/BottomNavBar.axaml.cs`), which is the single source of truth for tab identity across both
layout bands.

The desktop has exactly three destinations, in this order: **Главная · Аккаунт · Настройки**. The
wide layout renders them as a left rail, the compact layout as a bottom bar; both drive the same
`AppTab` state, so the tab survives a width change. All three are present in every state — «Аккаунт»
does not collapse when signed out, because that would remove the only route to the sign-in gate
exactly when it is needed.

Consequences, so nobody "fixes" them by accident:

- `11-app-structure.md` §2.1's four-destination model and its §3.2/§4.2 screens apply to **Android
  only**. Section 2.3's argument for a Серверы destination is recorded but overruled.
- `Geo.Nav.Servers` is declared and unused **on purpose**. `Nav_Servers` is not added to
  `Common/L.Shell.cs`.
- `Views/ServersView.axaml` and `Views/CompactServersView.axaml` sit on disk with zero construction
  sites. Editing either ships zero pixels. Before they are deleted, harvest the server search field
  in `CompactServersView` — it is the only one ever written for desktop.

The server list therefore lives inside Главная, and the problems a separate destination would have
solved are Главная's to solve there: in-place list filtering with a designed no-results state, a
visible per-row action control (today the seven per-server actions are reachable only from a
right-click menu), and real estate for the list in the compact band. Solving those inside Главная is
the work order. Adding a tab is not.

## Account, sign-in and billing

The account layer is `v2rayN/v2rayN.Desktop/Account/`:

- **`BackendConfig.cs` is the single configuration point** — base URL, Telegram bot username,
  subscription `User-Agent`, and every endpoint path. It is a port of the Android client's
  `auth/BackendConfig.kt`; the desktop has no generated `BuildConfig`, so the values are baked into
  this file. Nothing else should hard-code any of them.
- Auth is a **bearer JWT with a 7-day lifetime and no refresh endpoint**. `AuthTokenStore.cs`
  persists it; `AccountSession.cs` is the single source of truth for signed-in/out state and seeds
  itself from the token store on first access, so a returning user is already signed in.
- **Only the identity endpoint** (`GetMe`, via `AccountRepository.RefreshProfile`) may wipe a
  session on 401. A 401 or 403 anywhere else surfaces as a plain error and must **not** sign the user
  out. This is deliberate; do not "simplify" it.
- `SubscriptionSyncManager.cs` feeds the account's subscriptions into the normal `ServiceLib`
  subscription plumbing rather than parsing anything itself.
- Sign-in covers Telegram (deep link to the bot, then polling for confirmation), email and password
  with a 2FA step, registration and email verification, magic links, password reset, and the
  app↔site SSO handoff, which returns through a `departamentvpn://auth?code=…` deep link or a pasted
  code. A Google button exists and is inactive. Payments and tariff checkout open a browser.
- `OnboardingView` is the first frame for a user with no subscriptions: full-width, no rail, no
  server list, no connect shield. `AccountSyncView` is the overlay that holds while a post-login
  import or a cold start is in flight.

**Subscriptions are per-item, not per-account.** A client can hold several, and every
subscription-scoped action — devices, upgrade, rename, QR — must carry the *selected* subscription's
uuid rather than defaulting to the root one. `AccountView` is built on that rule; new
subscription-scoped features must follow it.

**Never infer a trial from the squad or the tariff name.** In this deployment the trial squad is the
same Remnawave squad as the paid base tariff, so squad-based detection misclassifies paying
customers. The backend resolves it correctly and returns the flag; trust the flag.

## Release readiness

A current, evidence-anchored assessment of what would and would not survive a release —
publish shape, the inherited packaging scripts, the Windows elevation model, the updater — is in the
Android repository at `docs/agents/state/release-desktop.md`, with the Android counterpart beside it.
Read it before running any `package-*.sh` or cutting a tag; several of those scripts still assume the
upstream binary name, and one of them moves `HEAD`.

`docs/agents/state/STATE-OF-WORK.md` records what is genuinely built versus what is only specified.

## Licence and attribution

GPL-3.0, unchanged from upstream. The full text is in [`LICENSE`](LICENSE), whose appendix carries
upstream's notice: `v2rayN Copyright (C) 2019-Present 2dust`. Copyright in the upstream code stays
with the v2rayN authors; the Departament changes are released under the same licence, and any
redistribution of a build must carry it.

Third-party components keep their own licences: the `GlobalHotKeys` submodule
(`v2rayN/GlobalHotKeys/LICENSE`), the Golos Text font
(`v2rayN.Desktop/Assets/Fonts/GOLOS-TEXT-LICENSE.txt`), the core binaries the app runs (Xray,
sing-box and others — each with its own licence, none of them bundled here), and the NuGet packages
listed in `v2rayN/Directory.Packages.props`.
