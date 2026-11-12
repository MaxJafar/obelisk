# Contributing

Start with a reproducible issue or a focused pull request. Keep the interface monochrome, flat and legible, with circular geometry and meaningful motion. Respect reduced-motion settings. New providers should work from explicit user configuration and fail clearly when data is unavailable.

Run these checks before submitting:

```powershell
python -m unittest discover -s tests -v
dotnet run --project windows/JarvisHud.HomelabChecks -- examples/demo-snapshot.json
dotnet run --project windows/JarvisHud.AudioChecks
node --check tauri/web/app.js
node --check tauri/web/demo.js
python tools/audit-public.py
```

Also run `go test ./...` from `audio-engine` and `tauri/Build-Obelisk.ps1` for native changes. Tests do not require a Gateway, microphone, voice models or real lights. For browser verification install `tests/requirements.txt`, install Playwright Chromium, and run `python tests/check_demo.py`.

After changing the interface or synthetic fixture, run `tools/Build-Demo.ps1` and review both `tauri/web` and `docs/tour`. The exporter accepts only the checked-in fixture with every device explicitly marked synthetic. `tools/Build-Icon.ps1` mechanically packages the generated PNG for Windows.

Never include live screenshots, session dumps, network inventory, SSH files, recordings, `.env` files or application data. Use documentation addresses and `.example.test` names in examples. Review `git diff --cached` and run Gitleaks before publishing. The public-data policy script prints locations/categories, not secret values. Avoid attaching raw failure dumps from your live system.

Contributions are provided under the repository's 0BSD license. Third-party code and assets need compatible terms and attribution.
