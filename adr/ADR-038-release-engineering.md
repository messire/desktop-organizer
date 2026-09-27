# ADR-038: Release Engineering — Installer, CI, and Manual Update Check

Status: Accepted (2026-09-27)

## Context

Before this package the project had no CI, no root-level `README`/`LICENSE`/`CHANGELOG`, an installer whose
publisher/support URLs were placeholders, no code-signing story, and no way for an installed copy to learn
a newer version exists short of the user manually checking GitHub. None of this blocks the application from
working — it blocks it from being *releasable* as a 1.0: a stranger downloading an unsigned installer with
placeholder URLs, from a repository with no license file, has no way to evaluate trust or find support.
This package (release-hardening plan §7, G1 + G2) closes that gap without introducing any new persisted
state, telemetry, or automatic network activity — the project's "no telemetry" claim (README) had to
remain true after this package, not just before it.

## Decision

### G1 — installer, CI, and repository documents

**Installer publisher/support links.** `installer/DesktopOrganizer.iss`: `AppPublisherURL`/`AppSupportURL`
now point at `https://github.com/messire` (`MyAppURL`) instead of a placeholder; `MyAppPublisher` stays
`"khagen"` unchanged — the orchestrator's Q4 decision (`release-hardening-1.0.md` §10) was variant B (MIT
license under "messire" as copyright holder, installer publisher name left as-is), not variant A (renaming
the publisher too).

**Optional code signing, never blocking an unsigned build.** `installer/build.ps1` signs the publish
output's `DesktopOrganizer*.exe/.dll` via `signtool` and then hands Inno Setup its own `SignTool=dosign`
directive (guarded by `#ifdef SignEnabled` in the `.iss`, enabling `SignedUninstaller=yes` too) — but only
when `DESKTOPORGANIZER_SIGN_CERT` is set; absent it, the script prints an explicit "unsigned build" notice
and produces exactly the same unsigned output as before this package. `DESKTOPORGANIZER_SIGN_CERT` accepts
either a `.pfx` file path or a certificate-store SHA1 thumbprint. A review finding (M5/L8) hardened the
design further: **the Inno Setup sign command line always signs by certificate-store thumbprint, never by
`/f <pfx> /p <password>`** — ISCC echoes its own `SignTool=` command line into its compile log, so a
plaintext password there would be a real leak; a `.pfx` is imported into `Cert:\CurrentUser\My` first (the
script removes it again in a `finally` block, and CI additionally removes it in its own `always()` step as
a second, independent safety net). The password is masked in this script's own console output and never
appears in any log ISCC itself produces. `-p:ContinuousIntegrationBuild=true` was added to the publish step
(deterministic `/_/`-style paths in stack traces — supports the crash-log privacy scrubbing ADR-033 relies
on).

**Root `README.md`/`LICENSE`/`CHANGELOG.md`.** `README.md` gained an English section and a Russian section
(what the app is, install/uninstall, feature list, where data/logs live, `--diag`/`--restore-state-backup`,
known limitations, license/support link). `LICENSE` is MIT, copyright "messire" (Q4 decision, above).
`CHANGELOG.md` follows Keep a Changelog, built from the repository's own squash-commit history
(`vX.Y.Z` tags/commit bodies on `master`) — versions before `0.1.0` are explicitly not tracked (no tagged
history point exists before it).

**CI (`​.github/workflows/ci.yml`), Windows-only by necessity.** The app targets `net8.0-windows`/WPF and
its own tests exercise real Win32 P/Invoke (`INativeApi`) — there is no meaningful non-Windows runner for
this repository. `build-and-test` restores, builds Release with `-p:TreatWarningsAsErrors=true` (the
project-wide "0 warnings" gate from `CLAUDE.md` enforced mechanically, not just by convention), and runs
`dotnet test --filter "Category!=RequiresDesktop"` — a forward-looking trait filter (no test currently
carries it; it exists so a future test that genuinely needs a live interactive desktop can opt out without
touching the workflow again). A second job, gated on `refs/tags/v*`, builds the installer (optionally
importing a signing certificate from repository secrets, all three of which are optional — CI never fails
just because signing secrets are absent) and attaches `Setup.exe` to the tag's GitHub Release.
Least-privilege `permissions: contents: read` at the workflow level, `contents: write` only on the one job
that needs it; the `installer` tool version and the `action-gh-release` action are both **pinned**
(`innosetup --version=6.7.1`, `softprops/action-gh-release@<commit-sha> # v2.6.2`) rather than tracking a
floating tag — an upstream release being yanked or a third-party action's tag being moved must not silently
change what CI builds with.

### G2 — manual update check and About links

**No automatic network activity, no persisted state.** There is no "last checked" timestamp, no
autocheck-at-startup — the only way `IUpdateCheckService.CheckForUpdatesAsync` ever runs is the user
clicking "Check for updates" in the About section. The result lives only in `AboutViewModel`'s own
in-memory state for the session.

**`SemVersion`** (`Core/Versioning`) — a SemVer 2.0 parser/comparer (strips a leading `v`, understands
`+build`/`-prerelease`, prerelease sorts below release) used both to compare the running build's own
`AppVersionInfo.Display` against the latest tag and, implicitly, to validate that tag is well-formed at all
(an unparsable current-build version — a dev/local build — reports `Unexpected` rather than guessing).

**`GitHubReleaseUpdateChecker`** (`Services/Updates`) — one GET against
`https://api.github.com/repos/messire/DesktopOrganizer/releases/latest`
(`UpdateEndpoints.ApiLatestReleaseUrl`; `UpdateEndpoints.Repo`/`ReleasesUrl`/`IssuesUrl`/`ProjectPageUrl`
are the complete, compile-time-constant set of external endpoints this application will ever contact or
open — there is no settings surface to point it elsewhere). Owns exactly one `HttpClient` for the DI
container's own lifetime, 10-second timeout, `User-Agent: DesktopOrganizer/<AppVersionInfo.Display>`,
`Accept: application/vnd.github+json`, a 256 KB response-body cap (anything larger is treated as an invalid
response rather than buffered in full). `404` → `NoReleases` (the repository has no releases yet, or isn't
public); `403`/`429` → `Failed(RateLimited)`; any other non-success or a malformed/oversized/unparsable body
→ `Failed` with a specific `UpdateCheckFailureKind`. **Never throws** — every failure mode is caught inside
the checker itself and turned into a result value.

**`html_url` is validated structurally, not by a naive prefix check (M4 finding).**
`UpdateEndpoints.TryGetTrustedReleaseUrl` parses the release API response's own `html_url` via `Uri` (scheme
must be `https`, host must be exactly `github.com`, path must start with `/messire/DesktopOrganizer/`) —
this closes a look-alike-domain/redirect class of trick a bare `StartsWith("https://github.com/messire/")`
string check would be more fragile against; an untrusted or absent `html_url` falls back to the fixed
`ReleasesUrl` instead of being opened at all.

**`IExternalLinkLauncher`** (`UI/Services`) — every external open goes through `OpenUrl`/`OpenFolder`, never
a raw `Process.Start` at each call site. `OpenUrl` (M4 finding) only launches a well-formed, absolute
`https://` URL via `ShellExecute` — a `javascript:`/`file:`/bare-`http://`/malformed string is logged and
refused, never handed to the shell. Every failure (no default browser, a declined UAC prompt, a deleted
folder) is logged and swallowed — these are always convenience actions, never on a critical path.

**`AboutViewModel`** (`UI/ViewModels`) exposes `CheckForUpdatesCommand` (async, `IsChecking` disables the
button while in flight), `OpenReleaseCommand` (opens the specific release URL from the last
`UpdateAvailable` result), `OpenProjectPageCommand`/`ReportProblemCommand` (the two fixed
`UpdateEndpoints` links), and `OpenLogsFolderCommand` (creates the logs folder if missing, then opens it) —
a small naming deviation from the original plan sketch (`OpenReleasesCommand` → split into
`OpenReleaseCommand` + `OpenProjectPageCommand`, since the About section links to both the author's project
page and, only when relevant, the specific release). Status text is fully localized (`Settings_About_
Update_*` keys) and re-resolves on a hot language switch via `ILocalizationService.CultureChanged`, matching
the convention `MainWindowViewModel.LanguageOptions` already established (ADR-032).

## Consequences

Positive:
- The project is now releasable as a stranger-facing 1.0: a license, a README that explains what the
  installer does and where its data lives, and a CI pipeline that gates every merge on the project's own
  "0 errors, 0 warnings" standard.
- Signing is fully optional and fails safe — CI, a local dev machine, and a future maintainer with a real
  certificate all produce a working installer either way, with no code branch that requires a certificate
  to build at all.
- The update check adds the project's very first outbound network call, scoped as narrowly as the plan
  could make it: one constant URL, no autocheck, no persisted history, structurally-validated redirect
  target, hard body-size cap, and a launcher that refuses anything but an `https://` URL.

Negative:
- `MyAppPublisher` staying "khagen" while `LICENSE` says "messire" is an intentional but visible asymmetry
  a future maintainer should not "fix" without re-confirming the Q4 decision this ADR records.
- GitHub's unauthenticated rate limit (60 requests/hour per IP) is shared across every DesktopOrganizer
  install checking from behind the same NAT/proxy — acceptable for a manual button, not for anything more
  frequent; there is deliberately no autocheck specifically because of this.
- Pinning tool versions/action SHAs (Inno Setup, `action-gh-release`) means a future maintainer must
  manually bump them — a small, recurring maintenance cost traded for supply-chain predictability.

## Manual verify (Win10/Win11)

Installer's "Programs and Features" entry shows the publisher/support links; SmartScreen behavior with and
without a real signing certificate; "Check for updates" online and offline (network failure produces a
readable, non-crashing message); a hot language switch while a status message is shown; every link opens in
the system default browser; CI green on a clean clone including the tag-triggered installer job (manual —
requires a real push, not reproducible from this environment).

## Related

- ADR-033 (Crash Safety and App Log) — `AppVersionInfo`/`ContinuousIntegrationBuild=true` support the same
  crash-report privacy/determinism goals that ADR relies on.
- ADR-032 (UI Localization) — the About section's update-check status text follows the same hot-switch
  convention this ADR's `AboutViewModel` reuses.
- `docs/PLANS/release-hardening-1.0.md` §7 (packages G1/G2).
