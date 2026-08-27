# VoicePaste

VoicePaste is a Windows tray application for push-to-talk dictation. The current implementation
includes the .NET 10/WPF shell, pass-through Right Ctrl Raw Input, WASAPI capture, bounded chunked
temporary storage, OpenAI `gpt-transcribe`, target identity checks, safe clipboard leasing, simulated
paste, Windows Credential Manager storage, deterministic session coordination, and automated tests.

## Configure transcription

The app uses OpenAI `gpt-transcribe` as the implemented MVP candidate. Recorded audio is sent to the
OpenAI API, so an API key, network access, and API billing/quota are required. The key is stored for
the current Windows user in Windows Credential Manager and is never written to `settings.json`.
Settings can search and delete saved `VoicePaste:*` credential entries by name and saved date; secret
values are never displayed by the key manager.

1. Start VoicePaste with Settings visible.
2. Enter an OpenAI API key and choose **Save key securely**.
3. Focus an editable field in another application.
4. Hold **Right Ctrl** while speaking, then release it to transcribe and paste.

```powershell
& '.\.dotnet\dotnet.exe' run --project src\VoicePaste.App\VoicePaste.App.csproj -c Debug -- --show-settings
```

The adapter supports Vietnamese-English mixed hints and splits long PCM recordings into ordered WAV
requests below the provider's 25 MiB file limit. The candidate has deterministic adapter tests, but
its live smoke, frozen mixed-language corpus, latency, privacy-policy, and 60-minute gates remain
pending. It must not be described as the verified MVP winner until those gates pass.

## Build and test

The repository pins .NET SDK `10.0.302`. If `dotnet` is installed globally, use it directly. This
workspace also supports a repository-local SDK at `.dotnet/` (ignored by Git).

```powershell
& '.\.dotnet\dotnet.exe' restore VoicePaste.slnx
& '.\.dotnet\dotnet.exe' build VoicePaste.slnx -c Release --no-restore
& '.\.dotnet\dotnet.exe' test --solution VoicePaste.slnx -c Release --no-build --no-restore
```

Run the framework-dependent Debug build through the repository-local runtime and open Settings:

```powershell
& '.\.dotnet\dotnet.exe' run --project src\VoicePaste.App\VoicePaste.App.csproj -c Debug -- --show-settings
```

Launching the Debug `.exe` directly requires a system-wide .NET 10 Desktop Runtime. The command
above avoids that requirement by using the repository-local runtime. Without `--show-settings`,
VoicePaste intentionally starts minimized in the system tray.

Create a self-contained x64 publish:

```powershell
& '.\.dotnet\dotnet.exe' restore src\VoicePaste.App\VoicePaste.App.csproj -r win-x64 -p:SelfContained=true
& '.\.dotnet\dotnet.exe' publish src\VoicePaste.App\VoicePaste.App.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts\publish\win-x64
```

## Safety behavior

- Right Ctrl is observed without suppression and Left Ctrl does not trigger the default binding.
- Capture uses a bounded channel, five-minute maximum PCM chunks, and a 256 MiB free-space reserve.
- Provider uploads use 16 kHz, 16-bit, mono PCM WAV segments capped at 24 MiB and are recombined in
  recording order; request segmentation is not a recording-duration limit.
- Auto-paste runs only while the originally captured process/window remains foreground and at a
  compatible integrity level.
- Existing clipboard content is overwritten only when it can be safely materialized within 16 MiB;
  restoration requires the VoicePaste token, sequence number, and owner to remain unchanged.
- Temporary audio and secrets are excluded from logs; credentials are stored through Windows
  Credential Manager, not settings JSON.
