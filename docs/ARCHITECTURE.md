# Architecture

Obelisk has a Tauri 2 shell with a framework-free SVG/canvas interface. The shell owns a .NET 10 bridge through newline-delimited JSON pipes. The bridge owns the Gateway connection, collection lifecycle, voice turns, metering and lighting client. No Tauri shell or arbitrary filesystem plugin is exposed to the UI.

| Directory | Responsibility |
| --- | --- |
| `tauri/web` | Flat interface, map interaction, agent ring and user waveform; browser-only synthetic adapter. |
| `tauri/src-tauri` | Window, IPC, packaged resource discovery and bridge process lifetime. |
| `windows/ObeliskBridge` | Validated operations and safe state/configuration events. |
| `windows/JarvisHud/Core` and `Gateway` | Agent state, device identity, DPAPI and OpenClaw protocol. The internal namespace retains its original name. |
| `windows/JarvisHud/Audio` | WASAPI, streaming PCM, phrase scheduling, live transcription and level extraction. |
| `windows/JarvisHud/Homelab` | Configured Python collection, typed snapshots, topology and guarded Docker actions. |
| `audio-engine` | Go supervisor for a local llama.cpp Qwen worker. |
| `tools/DemoExport` | Generate browser fixture views from synthetic data using the real topology builder. |

The frontend sends named operations; it cannot supply arbitrary shell commands. Secrets are accepted only by configuration operations and never returned in configuration events. Device/endpoint state comes from the latest snapshot. Stale snapshots do not enable controls or animated live links.

Collection is single-flight with bounded SSH/process timeouts. Configured hosts are collected concurrently. Full snapshots refresh every 30 seconds while the dashboard is visible; hidden windows pause polling. Links represent ownership and logical membership, not physical network discovery. Uptime Kuma history is distinct from current endpoint reachability.

## Audio flow

Explicit microphone turns feed WASAPI samples to both a separate waveform and the transcription stream. Recognized text becomes a Gateway turn. Cumulative agent text is divided into ordered phrases; the first phrase is shorter to reduce time to first audio. Revisions to already-spoken text stop that speech pipeline explicitly.

The Go process owns a single CUDA worker in a Windows kill-on-close job. Authentication is required on both supervisor and worker endpoints. PCM starts flowing before a full utterance is synthesized. Bounded transport and one phrase of lookahead limit buffering. Playback-position features drive the agent visualizer and optional RGB; cancellation propagates through inference and playback. English/Russian routing selects the corresponding private reference WAV.

## Lighting adapter

The optional client opens the same-user pipe `LightDesk.JarvisAudio.v1`. Each 12-byte frame starts with ASCII `LDR1`; byte 4 is `1` for a level update or `0` for restoration, byte 5 is level 0–255, bytes 6–8 are RGB, and the remaining bytes are reserved. The adapter acknowledges with one byte: `1` accepted/restored, `2` manual control has priority, `0` unavailable.

The separate adapter must restrict access to the current user, save device state, restore on stop/disconnect, and enforce a stale-frame watchdog. Obelisk sends a final restoration request and waits briefly for acknowledgment; it cannot guarantee restoration if an unrelated adapter fails. Public tests use a protocol peer, not hardware.

## Extend

Add providers to the collector with explicit configuration and a narrow metadata schema. Keep unavailable values nullable and report coverage. Add graph types in `TopologyGraph.cs`; regenerate the synthetic tour afterward. New service actions need server-side allowlists, state checks, meaningful tests and a defined failure path. Do not infer authorization from a bookmark.
