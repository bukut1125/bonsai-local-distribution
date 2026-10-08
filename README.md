# Bonsai Local Distribution

Windows bootstrapper for installing the existing Bonsai runtime profile and configuring the existing Local Model Control Center. The package keeps model/runtime tuning in the GitHub manifest and profile files so a published setup executable can fetch the current stable configuration.

## Current delivery status

The Windows setup and update code, GitHub manifest, resumable downloader, model/profile configuration generator, existing-launcher adapter, and local model-extension agent are pushed to the public control-plane repository, `https://github.com/bukut1125/bonsai-local-distribution`. The self-contained Setup EXE and portable ZIP are published in the [1.0.0 Preview 3 release](https://github.com/bukut1125/bonsai-local-distribution/releases/tag/v1.0.0-preview.3). Preview 3 supersedes Preview 2 because it includes the Hermes Agent installation and profile connection.

The setup targets `%LOCALAPPDATA%\BonsaiLocal`, reads the current stable manifest, writes the existing launcher registry, and runs a loopback model/inference check before it reports success. The default model is the pinned Ternary Bonsai 2 27B PTQ1_0 GGUF. The clean-machine install and inference path remains unaccepted because the development computer does not have enough free space for that full installation. The NVIDIA 8 GB profile is still a candidate: this computer has an RTX 5070 12 GB, so an 8 GB hardware inference claim is not made.

The setup also installs the pinned Hermes Agent core under the selected Bonsai install root when no usable Hermes executable is already available. It uses the upstream Windows bootstrap in per-user, non-interactive mode; the verified bootstrap hook that would write the user's PATH is disabled, and the launcher starts Hermes by absolute executable path. No administrator rights or manual PATH/environment setup are required. The existing `BonsaiLauncher.exe` manages model start/stop and context profiles, then synchronizes the selected profile into its isolated Hermes home. Hermes receives the same bounded Bonsai GGUF integration through a local stdio MCP server with five typed hardware/model tools. Browser and computer-use extras are omitted from the Hermes bootstrap.

## Local checks

```powershell
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --diagnose
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --test-selector
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --test-manifest
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --test-downloader
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --test-hermes
.\scripts\Test-AgentMcp.ps1 -SetupExecutable .\dist\BonsaiSetup.exe
.\scripts\Test-HermesProfile.ps1 -LauncherSourceRoot 'C:\path\to\Hermes-LocalModels' -DistributionRoot .
```

`--diagnose` reports Windows version, GPU/VRAM source, RAM, CPU/core/thread/AVX capability, target-drive free space, and the selected hardware profile. It does not start a model or write files unless `--output` is supplied.

`--test-selector` runs small positive/negative fixtures for NVIDIA 8/12/16 GB classes, low VRAM, no GPU, and non-NVIDIA GPU fallback.

`--test-manifest` checks the central manifest's model/backend/profile/asset references. `--test-downloader` seeds a 1 MiB HTTP Range prefix, resumes the pinned 18.8 MB CPU runtime, and verifies the release SHA-256.

`scripts\Test-AgentMcp.ps1` drives the published setup executable over MCP stdio. It verifies the legacy initialize flow and current 2026-07-28 discovery flow, tools/list, hardware/model tools, public Hugging Face GGUF search and metadata, including repository revision, file size and LFS SHA-256. Its install call deliberately supplies an invalid context size and verifies rejection before weight download or registry write. It does not download a model or start inference.

`--test-hermes` downloads the pinned upstream Windows install script, checks its SHA-256, and verifies the path-free patch without executing the installer. `scripts\Test-HermesProfile.ps1` runs the packaged profile sync scripts under Windows PowerShell 5.1, switches between 64K and 128K context profiles, and checks the local API/MCP paths and that user PATH remains unchanged.

`scripts\Test-PortableLauncherPreflight.ps1` stages the existing launcher scripts under a temporary install root, uses directory junctions for the already installed model/runtime, and invokes only the runtime's device-list preflight. It does not load the model or start the server. The 8 GB profile fixture passed on the actual 5070 host, which proves the profile values reach the runtime command but does not prove behavior on an 8 GB GPU.

## Release build

Build the setup and portable ZIP from PowerShell 7:

```powershell
.\scripts\Build-Portable.ps1 `
  -LauncherSourceRoot 'C:\path\to\Hermes-LocalModels' `
  -ManifestUrl 'https://raw.githubusercontent.com/bukut1125/bonsai-local-distribution/main/manifests/stable.json'
```

The script publishes the existing WPF Control Center as a self-contained single-file `BonsaiLauncher.exe`, packages model/Hermes launch and profile-sync scripts into the setup executable, publishes `dist\BonsaiSetup.exe` plus `dist\BonsaiSetup-portable.zip`, and runs the Hermes profile and MCP protocol checks against the packaged components. No model weight is stored in this repository. The released setup embeds the stable raw GitHub manifest URL; later hardware, model, runtime, Hermes commit and extension-profile tuning changes are read at install/update time from GitHub.

The manifest and hardware profile file remain separate so the setup fetches the selected profile catalog from the same repository via the manifest's relative `hardware_profiles_url`.

## Hardware detection

- NVIDIA name, total VRAM, and driver version: `nvidia-smi`, when the NVIDIA driver provides it.
- GPU fallback: DXGI adapter descriptions and dedicated video memory.
- Physical/available RAM: `GlobalMemoryStatusEx`.
- CPU model: Windows hardware registry; core/thread count: Windows processor topology APIs.
- AVX2/AVX-512: .NET x64 intrinsic support flags.
- Install-drive capacity: `DriveInfo` for the selected install directory.

## Local model-extension agent

The Control Center's “自主接入本地 GGUF 模型” panel submits the task to the currently Ready Bonsai model through `127.0.0.1:18080/v1`. The in-process agent can call only the Bonsai model tools; it has no shell or code-execution tool. It can read local hardware/backend information, search public Hugging Face GGUF repositories, inspect an immutable repository revision and its GGUF metadata, then download one selected GGUF with Range resume and SHA-256 verification. The extension policy and generated profile defaults come from `model_extension` in `manifests\stable.json`.

The agent registers downloaded models in `config\user-model-registry.json` and the launcher registry. Updates merge those local entries back after refreshing the GitHub-controlled defaults. Registration means the weight file passed size/hash checks and the model/profile are present in the launcher; it does not mean the new model has started or passed inference. The user selects and starts the new profile in the Control Center.

An external MCP-compatible agent can use the same local integration API by launching `BonsaiSetup.exe --mcp --install-dir "<BonsaiLocal>"` as an stdio MCP server. It supports the 2026-07-28 stateless protocol and legacy initialize clients. `BonsaiSetup.exe --agent-task "<request>" --install-dir "<BonsaiLocal>"` runs the built-in local Bonsai agent non-interactively. Both entry points expose only metadata search/inspection and typed GGUF registration tools.

## Hermes Agent and model manager

On a clean machine, Setup installs the Hermes Agent core from the immutable source commit in `manifests\stable.json` under `runtime\hermes-agent` and `runtime\hermes-core-home`. If a usable Hermes executable is already found, Setup reuses it. The official bootstrap is hash-checked, then its one user-PATH registration call is disabled; the WPF launcher starts Hermes with the installed absolute path. The install stays in the current user context and does not request administrator elevation. Browser and computer-use extras are skipped; Hermes CLI/TUI, MCP, and the other base agent tools remain available.

`BonsaiLauncher.exe` is the model manager: it starts and stops the selected runtime and switches among GitHub profile context sizes. Each Hermes launch synchronizes that selected context into the per-profile `HERMES_HOME` and connects Hermes to the local Bonsai endpoint and the five typed model-extension MCP tools. When Windows Terminal is unavailable, the launcher opens Hermes in a PowerShell console.

## Delivery acceptance

- PASS: native `nvidia-smi` detection on this Windows 11 host; an independently forced DXGI fallback reports the NVIDIA and Intel adapters.
- PASS: selector examples for 8/12/16 GB, low VRAM, CPU-only, non-NVIDIA fallback, and manual profile override.
- PASS: manifest reference check, actual resumable CPU-runtime transfer and release digest check, and PowerShell 5.1 syntax parsing for the packaged launcher scripts.
- PASS: portable registry/device-list preflight on the installed RTX 5070 host, without starting inference.
- PASS: published raw manifest/profile URLs; packaged Setup EXE self-diagnose, selector, manifest and cached downloader SHA checks.
- PASS: packaged Setup EXE MCP subprocess smoke across legacy and 2026-07-28 protocol flows, live Hugging Face GGUF search/inspection, and pre-download rejection of invalid profile input.
- PASS: pinned Hermes bootstrap source digest, no-elevation/no-user-PATH hook check, and Windows PowerShell 5.1 Hermes profile/MCP configuration with context switching.
- USER-PENDING: full Hermes core installation from the published EXE, clean-machine download of the 5.95 GB Bonsai 2 model and installation-time inference, real Hermes-to-Bonsai MCP tool calls, friend-side RTX 5060 8 GB inference, and visual UI interaction.

The current Bonsai artifact is the publisher's Ternary Bonsai 2 27B PTQ1_0 GGUF (Apache-2.0), with the existing OrcaRouter LoRA adapter (Apache-2.0); this is not a conventional Q4 GGUF. The Windows runtime is pinned to the PrismML `b10709-9a9394a` build (MIT), which supports the model's PTQ1_0 and Prism Hadamard backend requirements. The 8 GB layer/context values are centralized in `manifests/stable.json` and remain candidates until an actual 8 GB inference result exists.

Primary source links: [Bonsai 2 model repository](https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf), [pinned PrismML Windows runtime release](https://github.com/PrismML-Eng/llama.cpp/releases/tag/prism-b10709-9a9394a), [pinned Hermes Agent repository](https://github.com/NousResearch/hermes-agent/tree/d6284c675ea86ff746c5565e171e4877462dafc1), [Hermes Windows Native guide](https://hermes-agent.nousresearch.com/docs/user-guide/windows-native), [Hermes MCP configuration reference](https://hermes-agent.nousresearch.com/docs/reference/mcp-config-reference), [Hermes model-provider plugin guide](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/developer-guide/model-provider-plugin.md), [pinned OrcaBonsai adapter source](https://github.com/Continuum-AI-Corp/OrcaBonsai-27B-Uncensored), and [Microsoft .NET single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).
