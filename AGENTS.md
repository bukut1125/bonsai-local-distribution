# Bonsai Local Distribution project rules

This project builds the Windows bootstrapper and the central configuration bundle for installing the existing Bonsai model through the existing Hermes Local Models Control Center.

## Scope and ownership

- Keep this distribution project separate from the canonical `Hermes-LocalModels` source tree. The installed launcher package must remain compatible with that project's `config/model-registry.json`, `config/local-runtime.json`, `local/start-local-model.ps1`, and `local/stop-local-model.ps1` contracts.
- Do not create a second model control-center UI. The setup program handles detection, download, configuration, and first-run smoke checks; installed model control is delegated to the existing launcher.
- Do not modify the workspace-root `AGENTS.md` or global Codex files.
- Keep model weights, runtime archives, downloads, extracted release payloads, logs, and generated install state out of source control.

## Distribution contract

- `profiles/` and `manifests/` are the central control plane. Runtime arguments, model source revisions, profile choices, context values, GPU layer settings, and backend mappings belong there, not in executable code.
- Pin runtime/model/adapter sources and reuse existing verified source revisions and hashes while their assumptions remain valid. Never put multi-gigabyte model weights in Git.
- The default installation location is `%LOCALAPPDATA%\BonsaiLocal`; installation must not require Administrator rights.
- Support `--portable`, `--install-dir`, `--profile`, `--model`, and `--diagnose` without exposing runtime tuning flags to ordinary users.
- Keep the API bound to `127.0.0.1`.
- GPU memory pressure selects hybrid or CPU-capable profiles and is not itself a reason to reject installation. A live OOM may trigger at most two manifest-defined fallbacks.

## Change and acceptance rules

- Inspect applicable documentation and the real Windows toolchain before changing launch contracts.
- Prefer the smallest validation that can falsify the current change: profile fixtures for selector changes; a bounded artifact download for downloader changes; process/API/inference checks only when the real target runtime is intentionally available.
- Do not claim a 5060/8 GB profile is empirically accepted without evidence from an 8 GB target. Keep source/config/build evidence distinct from clean-machine runtime and inference evidence.
- Report each phase's acceptance conditions and any user-side clean-machine acceptance still pending.
