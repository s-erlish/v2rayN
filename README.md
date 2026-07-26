# Departament VPN - desktop client

This repository builds the desktop client of Departament VPN for Windows, Linux and macOS: a
VPN/proxy client that supervises a local core process (Xray, sing-box and the other cores upstream
supports) and gets its servers from the user's Departament subscription. It is one half of a
two-client product; the Android half lives in a sibling repository (see "The Android client").

The app works as a plain proxy client with no account at all. Signing in is what adds the
Departament side of the product: subscriptions, devices, tariffs, payments, promo codes, referrals.

## Relationship to upstream

This is a fork of [2dust/v2rayN](https://github.com/2dust/v2rayN). Everything that speaks the
protocols - core process management, config generation, subscription and share-link parsing,
routing, DNS, TUN, system proxy, statistics - is upstream's work in `ServiceLib` and is kept. What
this fork adds on top:

- the Departament account layer (`v2rayN.Desktop/Account/**`) and the screens that use it,
- a rebuilt Russian UI in `v2rayN.Desktop` following the design law (see "Design law"),
- product identity: the Avalonia host builds as `departament` (`AssemblyName`, `Product`,
  `AssemblyTitle`, `Company`), so it also identifies as `departament` in Task Manager.

Namespaces, the solution name and the `v2rayN*` project names are deliberately unchanged, so file
paths still match upstream and merges stay reviewable.

## Repository layout

Everything builds from `v2rayN/` (`v2rayN.sln`, or `v2rayN.slnx` for newer tooling).

| Project | What it is |
|---|---|
| `v2rayN/ServiceLib` | The shared, UI-free core: profiles and storage (SQLite), core config generation (`Services/CoreConfig`), link and subscription parsing (`Handler/Fmt`), core supervision (`Manager/CoreManager.cs`), speedtest, updates, WebDAV backup, logging. Both UI projects reference it. |
| `v2rayN/v2rayN.Desktop` | **The Departament client.** Avalonia, cross-platform. `Views/**` (AXAML), `ViewModels/**`, `Account/**` (Departament backend), `Manager/**`, `Assets/**`. |
| `v2rayN/v2rayN` | Upstream's WPF client, `net10.0-windows`, Windows only. Still in the solution; not what this fork ships. The Departament branch build publishes only `v2rayN.Desktop`. |
| `v2rayN/AmazTool` | Small console helper published next to the app; restarts it and applies a downloaded upgrade. |
| `v2rayN/GlobalHotKeys` | Git submodule (`https://github.com/2dust/GlobalHotKeys`), referenced as a project by `v2rayN.Desktop`. Required: without it the build fails with CS0246. |
| `v2rayN/ServiceLib.Tests` | xUnit v3 tests over config generation and link parsing. |
| `v2rayN/ServiceLib.UdpTest` | UDP probe helper library used by the speedtest path. |
| `package-*.sh` (repo root) | Upstream's release packaging for deb/rpm/macOS. Their output is still named after upstream. |

## Stack

C# on .NET 10 (`TargetFramework net10.0`, pinned in `v2rayN/Directory.Build.props`), Avalonia 12.1
with Semi.Avalonia, DialogHost.Avalonia and AvaloniaEdit, and ReactiveUI (with Fody) for view
models and commands. Compiled bindings are on by default, so a binding typo is a build error. All
package versions are pinned centrally in `v2rayN/Directory.Packages.props` - do not put a `Version`
on a `PackageReference`. Release builds are single-file with embedded symbols. On Windows,
`app.manifest` requests elevation so TUN mode can create the adapter.

`v2rayN.Desktop` is a submodule-dependent project, so clone with submodules or run:

```bash
git submodule update --init --recursive
```

## Build

```bash
export DOTNET_ROOT=/opt/dotnet PATH=/opt/dotnet:$PATH
cd v2rayN

dotnet build v2rayN.Desktop/v2rayN.Desktop.csproj -c Release
dotnet test ./ServiceLib.Tests
```

Self-contained publish, the way CI does it (`.github/workflows/build.yml`, and
`departament-branch-build.yml` for the Departament Windows build):

```bash
cd v2rayN
dotnet publish ./v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r linux-x64 -p:SelfContained=true -o ../dist
dotnet publish ./AmazTool/AmazTool.csproj -c Release -r linux-x64 -p:SelfContained=true -p:PublishTrimmed=true -o ../dist
```

Swap the RID for `win-x64`, `win-arm64`, `linux-arm64`, `osx-x64` or `osx-arm64`. The Windows
targets additionally pass `-p:EnableWindowsTargeting=true`.

Avalonia compiles AXAML during the build, so malformed markup fails the build rather than blowing
up at runtime. There is no excuse for shipping broken markup.

The **core binaries are not in this repository.** The app launches an external core from a per-core
directory under `bin/` next to the executable (`Utils.GetBinPath`, so `bin/xray/`, `bin/sing_box/`
and so on) and reads `bin/geoip.dat` and `bin/geosite.dat`. `departament-branch-build.yml`
downloads the latest Xray and sing-box releases plus the geo files into the published `bin/`;
otherwise the app's own update check fetches them (`ServiceLib/Services/UpdateService.cs`). A build
with an empty `bin/` starts, but there is no core for it to run.

## Verifying a change in this environment

Environment setup and the build gate for both clients live in the Android repository (in this
environment: `/home/user/dp`), because one gate covers the product:

```bash
bash /home/user/dp/docs/agents/setup-env.sh              # once per container, idempotent
bash /home/user/dp/docs/agents/verify-build.sh desktop    # or: android | both
```

The gate passes only on `BUILD: SUCCESSFUL` **and** `NEW WARNINGS: 0`, compared against the
recorded warning baseline next to the script; it also reports whether the compiler actually ran,
because an up-to-date build proves nothing. It serialises builds behind a lock, so it can wait a
while before starting. Notes and the environment's sharp edges are in
`/home/user/dp/docs/agents/BUILD-VERIFY.md`.

## Account, subscriptions and payments

The account layer lives in `v2rayN/v2rayN.Desktop/Account/`:

- `BackendConfig.cs` is the single configuration point: base URL, Telegram bot username,
  subscription `User-Agent`, and every endpoint path. It is a port of the Android client's
  `auth/BackendConfig.kt`; the desktop has no generated `BuildConfig`, so the values are baked into
  this file. Nothing else should hardcode any of them.
- Auth is a bearer JWT with a 7-day lifetime and no refresh endpoint. `AuthTokenStore.cs` persists
  it; `AccountSession.cs` is the single source of truth for logged-in/out state and seeds itself on
  first access so a returning user is already signed in.
- Only the identity endpoint (`GetMe` via `AccountRepository.RefreshProfile`) may wipe a session on
  401. A 401 or 403 anywhere else surfaces as a plain error and must not log the user out. This is
  deliberate; do not "simplify" it.
- `SubscriptionSyncManager.cs` feeds the account's subscriptions into the normal `ServiceLib`
  subscription plumbing instead of parsing anything itself.
- Sign-in covers Telegram (deep link to the bot, then polling), email and password with a 2FA step,
  magic links, and the app-to-site SSO handoff, which returns through a
  `departamentvpn://auth?code=...` deep link or a pasted code. The Google button is present but
  inactive. Payments and tariff checkout open a browser.

The backend is the Departament bot backend, shared with the Android client, the web dashboard and
the Telegram mini app, so endpoint semantics are not ours to change unilaterally.

## The Android client

The Android client is a separate repository, a fork of
[2dust/v2rayNG](https://github.com/2dust/v2rayNG) (in this environment: `/home/user/dp`). The two
clients are one product under one design, not independent apps:

- both talk to the same backend, and this repo's `Account/**` is a deliberate port of the Android
  `auth/**`: same endpoint contract, same session rules, same DTO shapes;
- the design specifications cover both clients side by side, with a master plan per platform;
- the parity contract is written down: section 13 of `00-rules.md` (see "Design law") fixes what
  must be identical (destinations and their order, every Russian string for the same concept,
  default values, tokens, motion tempo) and what may differ (navigation shape, action surfaces,
  hover, haptics, shortcuts). A feature on one client and not the other is a logged parity gap, not
  a platform difference to be shrugged at.

## Design law

All UI work follows the specifications in the Android repository under `docs/design2026/` (in this
environment: `/home/user/dp/docs/design2026/`), starting with **`00-rules.md`**, which outranks
taste, habit and upstream precedent. It defines the tokens (one spacing scale, one accent, the
radius and type ramps), the absolute bans, the per-state requirements, the register of every
user-visible string (section 9), and the desktop mechanics (section 12): `DynamicResource` only so
theme switching works, keyboard-complete screens, a focus ring that is always visible, usable at
900x600, no nested scrollers, no default Fluent or Semi styling leaking through.

Then `03-direction.md` for the visual direction, `10-design-system.md`, and
`33-master-plan-pc.md` for this client screen by screen.

Read the rules before designing, not after.

## Licence and attribution

GPL-3.0, unchanged from upstream. The full text is in [`LICENSE`](LICENSE), whose appendix carries
upstream's notice: `v2rayN Copyright (C) 2019-Present 2dust`. Copyright in the upstream code stays
with the v2rayN authors; the Departament changes are released under the same licence, and any
redistribution of a build must carry it.

Third-party components keep their own licences: the `GlobalHotKeys` submodule
(`v2rayN/GlobalHotKeys/LICENSE`, WTFPL), the core binaries the app runs (Xray, sing-box and others,
each with its own licence and none of them bundled in this repository), and the NuGet packages
listed in `v2rayN/Directory.Packages.props`.
