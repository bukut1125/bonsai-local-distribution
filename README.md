# Bonsai Local Distribution

Windows bootstrapper for installing the existing Bonsai runtime profile and configuring the existing Local Model Control Center. The package keeps model/runtime tuning in the GitHub manifest and profile files so a published setup executable can fetch the current stable configuration.

## Current delivery status

The Windows setup and update code, GitHub manifest, resumable downloader, model/profile configuration generator, and existing-launcher adapter are implemented locally. The public control-plane repository is `https://github.com/bukut1125/bonsai-local-distribution`; the setup executable still needs to be built and published as a release asset.

The final clean-machine flow is implemented to retrieve the manifest and assets, install into `%LOCALAPPDATA%\BonsaiLocal`, write the existing launcher registry, run a loopback model/inference check, and leave the model Ready. It still needs actual GitHub publication and acceptance on a clean Windows PC. The NVIDIA 8 GB profile is a fallback-capable candidate; this computer has an RTX 5070 12 GB, so an 8 GB hardware inference claim is not made.

## Local checks

```powershell
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --diagnose
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --test-selector
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --test-manifest
dotnet run --project .\BonsaiSetup\BonsaiSetup.csproj -- --test-downloader
```

`--diagnose` reports Windows version, GPU/VRAM source, RAM, CPU/core/thread/AVX capability, target-drive free space, and the selected hardware profile. It does not start a model or write files unless `--output` is supplied.

`--test-selector` runs small positive/negative fixtures for NVIDIA 8/12/16 GB classes, low VRAM, no GPU, and non-NVIDIA GPU fallback.

`--test-manifest` checks the central manifest's model/backend/profile/asset references. `--test-downloader` seeds a 1 MiB HTTP Range prefix, resumes the pinned 18.8 MB CPU runtime, and verifies the release SHA-256.

`scripts\Test-PortableLauncherPreflight.ps1` stages the existing launcher scripts under a temporary install root, uses directory junctions for the already installed model/runtime, and invokes only the runtime's device-list preflight. It does not load the model or start the server. The 8 GB profile fixture passed on the actual 5070 host, which proves the profile values reach the runtime command but does not prove behavior on an 8 GB GPU.

## Release build

Once the GitHub repository exists, build the setup and portable ZIP from PowerShell 7:

```powershell
.\scripts\Build-Portable.ps1 `
  -LauncherSourceRoot 'C:\path\to\Hermes-LocalModels' `
  -ManifestUrl 'https://raw.githubusercontent.com/bukut1125/bonsai-local-distribution/main/manifests/stable.json'
```

The script publishes the existing WPF Control Center as a self-contained single-file `BonsaiLauncher.exe`, packages its launch/stop scripts and third-party license texts into the setup executable, and emits `dist\BonsaiSetup.exe` plus `dist\BonsaiSetup-portable.zip`. No model weight is stored in this repository. The released setup executable embeds the configured raw GitHub manifest URL; later profile/model/runtime tuning changes are read at install/update time from GitHub.

The manifest and hardware profile file remain separate so the setup fetches the selected profile catalog from the same repository via the manifest's relative `hardware_profiles_url`.

## Hardware detection

- NVIDIA name, total VRAM, and driver version: `nvidia-smi`, when the NVIDIA driver provides it.
- GPU fallback: DXGI adapter descriptions and dedicated video memory.
- Physical/available RAM: `GlobalMemoryStatusEx`.
- CPU model: Windows hardware registry; core/thread count: Windows processor topology APIs.
- AVX2/AVX-512: .NET x64 intrinsic support flags.
- Install-drive capacity: `DriveInfo` for the selected install directory.

## Delivery acceptance

- PASS: native `nvidia-smi` detection on this Windows 11 host; an independently forced DXGI fallback reports the NVIDIA and Intel adapters.
- PASS: selector examples for 8/12/16 GB, low VRAM, CPU-only, non-NVIDIA fallback, and manual profile override.
- PASS: manifest reference check, actual resumable CPU-runtime transfer and release digest check, and PowerShell 5.1 syntax parsing for the packaged launcher scripts.
- PASS: portable registry/device-list preflight on the installed RTX 5070 host, without starting inference.
- USER-PENDING: published GitHub raw manifest/release, real download of the 5.95 GB model to a clean machine, installation-time inference, friend-side RTX 5060 8 GB inference, and the complete no-development-tools Windows acceptance.

The current Bonsai artifact is the publisher's Ternary Bonsai 2 27B PTQ1_0 GGUF (Apache-2.0), with the existing OrcaRouter LoRA adapter (Apache-2.0); this is not a conventional Q4 GGUF. The Windows runtime is pinned to the PrismML `b10709-9a9394a` build (MIT), which supports the model's PTQ1_0 and Prism Hadamard backend requirements. The 8 GB layer/context values are centralized in `manifests/stable.json` and remain candidates until an actual 8 GB inference result exists.

Primary source links: [Bonsai 2 model repository](https://huggingface.co/prism-ml/Ternary-Bonsai-2-27B-gguf), [pinned PrismML Windows runtime release](https://github.com/PrismML-Eng/llama.cpp/releases/tag/prism-b10709-9a9394a), [pinned OrcaBonsai adapter source](https://github.com/Continuum-AI-Corp/OrcaBonsai-27B-Uncensored), and [Microsoft .NET single-file deployment documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).
