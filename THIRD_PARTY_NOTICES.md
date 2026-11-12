# Third-party components

Obelisk's first-party source and original branding use 0BSD. This does not replace the licenses of dependencies, models or separately downloaded runtimes. Source dependencies are resolved by Cargo, NuGet and Go; their lockfiles/manifests identify versions.

| Component | License / source |
| --- | --- |
| Tauri | MIT or Apache-2.0 — [upstream](https://github.com/tauri-apps/tauri) |
| .NET | MIT — [runtime](https://github.com/dotnet/runtime) |
| NAudio | MIT — [upstream](https://github.com/naudio/NAudio) |
| Bouncy Castle C# | MIT — [upstream](https://github.com/bcgit/bc-csharp) |
| Go and golang.org/x/sys | BSD-3-Clause — [Go](https://go.dev/LICENSE), [x/sys](https://cs.opensource.google/go/x/sys) |
| llama.cpp | MIT — [upstream](https://github.com/ggml-org/llama.cpp) |
| Qwen3-TTS | Apache-2.0 — [original model](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-Base), [GGUF distribution](https://huggingface.co/ggml-org/Qwen3-TTS-12Hz-1.7B-Base-GGUF) |
| NVIDIA CUDA runtime | NVIDIA's applicable license — obtained separately with the optional runtime download |
| Optional psutil / PyYAML | BSD-3-Clause / MIT — installed separately on telemetry hosts |
| Playwright / Gitleaks | Apache-2.0 / MIT — development and release verification tools |

No model weights, CUDA libraries, personal voice recordings, commercial UI kit, stock reference artwork, or private LightDesk server source is included. Preserve upstream notices when distributing compiled bundles; this initial release publishes source. User-supplied recordings remain the user's responsibility and are not covered by the source license.
