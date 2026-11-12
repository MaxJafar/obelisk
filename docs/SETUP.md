# Set up Obelisk

## Build

The desktop target is Windows x64. Install:

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
- [Rust](https://rustup.rs/) with the MSVC target, Visual Studio C++ build tools and a Windows SDK. Follow [Tauri's Windows prerequisites](https://v2.tauri.app/start/prerequisites/).
- [Go 1.27 or newer](https://go.dev/dl/) on PATH.
- [Python 3.10 or newer](https://www.python.org/downloads/windows/) on PATH. App Store execution aliases alone are not a Python runtime.
- Microsoft Edge WebView2 runtime, normally available on Windows 11.

From the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tauri/Build-Obelisk.ps1
.\tauri\publish\Obelisk.exe --preview
```

The build compiles the Go supervisor, publishes a self-contained .NET bridge, and builds Tauri using the committed Cargo lockfile. The executable and adjacent `bridge` directory form the application; keep them together. `-GoExecutable` can point to a custom Go executable. The execution-policy option applies only to that PowerShell process.

Launch without `--preview` for real configuration. Preview uses only the checked-in synthetic fixture and does not collect telemetry, connect a Gateway, record audio or control services. No installer, code signature or updater is provided yet.

## Connect your home lab

Private state defaults to `%LOCALAPPDATA%/Obelisk`. Create this directory and copy `examples/homelab.example.json` there as `homelab.json`. Never put your configured file into Git.

The minimal example observes only the current machine. For remote hosts, adapt `examples/homelab.multi-host.example.json`:

| Field | Meaning |
| --- | --- |
| `devices[].id` | Unique stable identifier, used to associate agents, services and controls. |
| `local` | Collect on the desktop computer. |
| `ssh` | An existing OpenSSH alias. Configure its user and key in your private SSH configuration. |
| `python` | Interpreter on that host, normally `python3`. Paths cannot contain spaces or shell metacharacters. |
| `ip`, `dns` | Your host's address and optional DNS/Tailscale name. Examples are documentation placeholders. |
| `providers` | Explicit subset of `codex`, `openclaw`, `hermes`, `homepage`, `kuma`. Empty means resource telemetry only. |
| `discoverTailscale` | Match configured hosts against local Tailscale status. Does not import every peer automatically. |
| `services` | Bookmarks with unique IDs, host ownership, credential-free URL and optional Docker container name. |
| `controls` | Per-device SSH policy and explicit container allowlist; empty by default. |

Authenticate to each SSH alias manually first and verify its host key. Collection is non-interactive, uses `StrictHostKeyChecking=yes`, and never configures or copies SSH credentials. Each remote host needs Python. `sudo` is optional and defaults off; use it only when your own remote policy permits the required commands without a password.

Install optional `psutil` for fuller CPU/memory/storage telemetry and `PyYAML` on hosts that read Homepage YAML. Without a provider's expected files or schema, Obelisk reports incomplete coverage rather than inventing values. Override metadata locations with `codexHome`, `openclawHome` or `hermesHome`; use `homepageFile`, `kumaUrl` and `kumaSlug` for those integrations.

Homepage widget credentials are not exported. URLs containing credentials, query strings or fragments are excluded. Bookmarks are probed only when their host matches the configured device or you explicitly set `probe: true`; HTTP health checks do not follow redirects. A reachable HTTP endpoint is not a deep application health check.

Controls are limited to Docker start/stop on explicitly allowed application names. The collector must observe that container, its complete ID and a recent running state. The control process verifies them again before acting and confirms the result. Infrastructure services should remain outside your allowlist unless you intentionally need to control them.

## Connect Jarvis

Open **Settings**, enter a `wss://` OpenClaw Gateway address, your configured agent ID and any required pairing token, then save. `ws://` is accepted only for loopback. Approve the device on your Gateway if prompted. This app does not install or administer the Gateway.

Gateway compatibility depends on its device-auth v3 and chat/session APIs. Start with text and confirm pairing before configuring voice. Your Gateway's provider configuration determines where messages are processed and stored.

Credentials are encrypted for the current Windows account. Leaving password fields blank retains existing values; they are never populated with saved secrets. To reset credentials, close Obelisk and remove the corresponding encrypted `.bin` files from its private data directory, then pair again. Do not share that directory as a bug report.

## Voice

The packaged engine targets NVIDIA CUDA on Windows. Qwen3-TTS 1.7B Base Q8 provides English and Russian reference-voice routing. Other languages, CPU-only latency, AMD/Intel backends and Kokoro are not configured by this release.

1. Run `tools/Prepare-Audio.ps1` to fetch the SHA-256-verified runtime and model files from the pinned manifests. About 3.1 GB is downloaded; allow additional space for extracted libraries.
2. Supply short, clean English and Russian WAV reference recordings you have permission to use with `tools/Import-Voices.ps1 -EnglishWav ... -RussianWav ...`. Keep them outside the repository. The helper checks the WAV container and size; inference validates decoding compatibility.
3. Enable local speech in Settings. **Warm voice** loads the model. Cold startup can take considerably longer than a warm response.
4. Enter your OpenAI transcription API key in Settings to use the microphone. Transcription is a paid external API; the project supplies no account or credit.

The microphone opens only for an explicit voice turn and closes after detected silence or manual submission. `Ctrl+Shift+Space` starts/submits a turn; **Stop** or `Escape` interrupts. Incoming agent phrases are synthesized before the complete reply arrives. The on-screen times are measurements for the current turn, not a latency guarantee. Model warmup, GPU load, transcription, Gateway/network time and inference all contribute.

LightDesk is optional. This repository includes only a client for its local `LightDesk.JarvisAudio.v1` protocol, not the separate hardware controller. An unavailable adapter does not prevent speech playback. See [architecture](ARCHITECTURE.md#lighting-adapter).

## Locations and troubleshooting

| Setting | Default |
| --- | --- |
| `OBELISK_DATA_DIR` | `%LOCALAPPDATA%/Obelisk` for preferences, encrypted keys and private telemetry cache |
| `OBELISK_AUDIO_DIR` | `audio` beneath the data directory |
| `OBELISK_PYTHON` | Python found on PATH; set to a full executable path to override |
| Audio endpoint | Loopback port `3920`, authenticated; worker uses a temporary loopback port |

The model download/import helpers accept `-AudioDirectory` for a custom location; set `OBELISK_AUDIO_DIR` to the same location before launching. Environment overrides are process settings, not saved by the app.

**No map:** check Python and SSH aliases; the app preserves unavailable inventory and marks stale data. **No sound:** check installed files, GPU driver compatibility and the private audio worker log. **Gateway offline:** check its URL, pairing approval and agent ID. **Port occupied:** close the previous app/engine; do not expose port 3920 on your network. **Missing token counts:** that provider may not report them; blank is intentional.
