# VoicePaste

VoicePaste is a Windows tray application for push-to-talk dictation. Hold **Right Ctrl**, speak in
Vietnamese, English, or a mix of both, then release the key to transcribe and paste the result into
the application that was active when recording started.

> [!IMPORTANT]
> VoicePaste is an MVP under active development. OpenAI `gpt-transcribe` is the implemented speech
> provider candidate, but the live mixed-language corpus, latency, privacy, long-duration, and
> Windows compatibility gates are still pending. It is not yet a production release.

## Features

- Global, pass-through **Right Ctrl** push-to-talk from the Windows system tray.
- No elapsed-time recording cutoff while the key remains held; audio is stored in bounded chunks.
- Vietnamese-English mixed-language hints and ordered transcription of long recordings.
- WASAPI microphone capture with device discovery and temporary-file cleanup.
- Focus-safe paste: VoicePaste validates the original target instead of stealing focus.
- Manual-copy fallback when automatic paste is unsafe or unavailable.
- OpenAI API keys stored for the current user in Windows Credential Manager.
- Collapsible key manager for finding and deleting saved `VoicePaste:*` credentials without
  displaying their secret values.

## Requirements

To run VoicePaste:

- 64-bit Windows 11. Windows 10 22H2 build 19045 is the compatibility baseline, but its manual gate
  has not yet been completed.
- A working microphone.
- Network access and an OpenAI API key with available API billing/quota.

The project targets .NET 6.0.36. Its repository-local .NET 10.0.302 SDK is the supported build
toolchain; the generated Windows x64 application is self-contained and does not require a separately
installed .NET runtime on the target computer.

## Quick start from source

Select the repository-local SDK when available, otherwise use the installed system SDK. Keep the
same PowerShell session for the remaining commands in this README:

```powershell
$dotnet = if (Test-Path '.\.dotnet\dotnet.exe') {
  (Resolve-Path '.\.dotnet\dotnet.exe').Path
} else {
  'dotnet'
}
```

Restore the solution and start VoicePaste with the Settings window visible:

```powershell
& $dotnet restore VoicePaste.slnx
& $dotnet run --project src\VoicePaste.App\VoicePaste.App.csproj -c Debug -- --show-settings
```

Then:

1. Enter your OpenAI API key and select **Save key securely**.
2. Focus an editable field in another application.
3. Hold **Right Ctrl** while speaking.
4. Release **Right Ctrl** to transcribe and paste.

Without `--show-settings`, VoicePaste starts minimized in the notification area. Double-click its
tray icon, or use **Open Settings** from the tray menu, to open Settings. The tray menu also provides
**Pause/Resume** and **Exit**.

If VoicePaste cannot safely return to the original target, it leaves the transcript in Settings for
manual copying instead of forcing a paste into another window.

## Build, test, and publish

Run the deterministic Release checks:

```powershell
& $dotnet restore VoicePaste.slnx
& $dotnet build VoicePaste.slnx -c Release --no-restore
& $dotnet test VoicePaste.slnx -c Release --no-build --no-restore
& $dotnet format VoicePaste.slnx --verify-no-changes --no-restore
```

The current automated suite contains 32 tests across the Core, Windows, OpenAI provider, and WPF
application projects. These tests do not replace the pending live provider, physical microphone,
paste-target, performance, accessibility, or Windows compatibility checks.

Create a self-contained Windows x64 build:

```powershell
& $dotnet restore src\VoicePaste.App\VoicePaste.App.csproj -r win-x64 -p:SelfContained=true
& $dotnet publish src\VoicePaste.App\VoicePaste.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  --no-restore `
  -o artifacts\publish\win-x64
```

Launch the published application:

```powershell
& '.\artifacts\publish\win-x64\VoicePaste.App.exe' --show-settings
```

## Privacy and safety behavior

- Recorded audio is sent to the OpenAI API for transcription. Review the provider's current privacy,
  retention, and regional-processing terms before using sensitive audio.
- API keys are stored in Windows Credential Manager, never in `settings.json`, and are excluded from
  application logs.
- The key manager shows credential metadata only. It never retrieves a key for display.
- Temporary audio uses a bounded channel, five-minute maximum PCM chunks, and a 256 MiB free-space
  reserve. Provider requests use 16 kHz, 16-bit, mono PCM WAV segments capped at 24 MiB; request
  segmentation is not a recording-duration limit.
- Automatic paste proceeds only when the original process/window is still foreground and has a
  compatible integrity level.
- If the clipboard contains browser-specific, rich, delayed-rendered, or otherwise unsafe formats,
  VoicePaste inserts Unicode text directly without overwriting that clipboard, then falls back to
  manual copy if the target does not accept direct input.
- Clipboard restoration occurs only if the VoicePaste lease token, sequence number, and owner remain
  unchanged. Existing clipboard data is replaced only when it can be safely materialized within
  16 MiB.
- Persistent transcript history is outside the current MVP, and temporary recording resources are
  deleted during normal cleanup paths.

## Project layout

- [`src/VoicePaste.Core`](src/VoicePaste.Core) — provider-neutral contracts, policies, and dictation
  session coordination.
- [`src/VoicePaste.Windows`](src/VoicePaste.Windows) — Raw Input, WASAPI capture, clipboard/paste,
  settings, and Windows Credential Manager adapters.
- [`src/VoicePaste.Providers.OpenAI`](src/VoicePaste.Providers.OpenAI) — the provisional OpenAI
  transcription adapter and long-recording segmentation.
- [`src/VoicePaste.App`](src/VoicePaste.App) — WPF composition root, Settings, overlay, and tray UI.
- [`tests`](tests) — deterministic unit and adapter tests.
- [`Requirement/VoicePaste_Requirements.md`](Requirement/VoicePaste_Requirements.md) — product SRS
  and acceptance criteria.
- [`specs`](specs) — architecture, implementation plan, test plan, decisions, provider evaluation,
  and requirements traceability.

## Specifications and implementation status

Start with the [specification index](specs/index.md). The most useful documents are:

- [Architecture](specs/architecture.md)
- [Implementation plan](specs/implementation-plan.md)
- [Test plan](specs/test-plan.md)
- [Speech-provider evaluation](specs/provider-evaluation.md)
- [Requirements traceability](specs/requirements-traceability.md)
- [Decision log](specs/decision-log.md)

Known incomplete MVP areas include full editable settings, start-with-Windows and installer support,
retry transcription/paste workflows, live provider qualification, and the remaining manual Windows
and accessibility gates. The specification documents are the source of truth for detailed status.
