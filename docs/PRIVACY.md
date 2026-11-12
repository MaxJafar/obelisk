# Privacy boundaries

Obelisk is a desktop control plane with explicitly configured integrations. Local speech output does not imply that the whole assistant is offline.

| Data | Destination and storage |
| --- | --- |
| Gateway text and agent requests | Your configured OpenClaw Gateway and whatever providers it uses. |
| Microphone audio | OpenAI transcription during an explicit microphone turn, when you configure an API key. |
| Speech output and voice references | Local Qwen inference and WASAPI playback. Reference recordings stay in the private audio directory. |
| Resource and agent metadata | Collected locally or over your verified SSH connections; cached in your private application data. |
| Gateway keys and transcription key | Windows DPAPI-encrypted files bound to your Windows account. |
| Local inference credentials | Random token files in the private audio directory; loopback service rejects browser origins and unauthenticated callers. These local tokens are not DPAPI encrypted. |
| Lighting | Bounded level/color frames over a local named pipe, never microphone audio or reply text. |

Providers are off by default. Agent collection can include session titles, workspace paths, model IDs, reported token usage and tool counters. Those are private even when they are not credentials. Do not publish caches, debug dumps or screenshots from your live system. Provider adapters do not export conversation bodies or environment files. The app does not contain analytics or a telemetry-upload service.

Data persists until you remove it. Close the app before deleting state. DPAPI protects secrets at rest but does not isolate them from other software running as the same Windows user; local inference token files rely on your profile's filesystem permissions. Use trusted local software and protect your Windows account.

The public tour has no backend, analytics, cookies, microphone access or local-device connections. Its fixture is invented. Normal GitHub Pages hosting can still receive standard web request metadata. README badges are served by GitHub and Shields.io.

The public source tree excludes configured host inventory, private network addresses, SSH configuration, personal recordings, models, tokens, logs, caches and build outputs. Release checks combine a tracked-file policy scan with Gitleaks. Scanners reduce risk; review additions before publishing. If a credential is ever exposed, revoke it with its issuer; removing a file alone is insufficient.
