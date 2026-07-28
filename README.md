# Hardware Busters GPU Test Suite

An open, reproducible .NET framework for GPU benchmark orchestration, measurement provenance, reporting, and profile authoring.

Hardware Busters GPU Test Suite includes both automated game testing and GPU Cooling Tester Evaluation.

This public source release contains code, safe defaults, a curated Hardware Busters Verified game pack, and disabled community samples. It does not contain benchmark results, machine settings, private operations material, game-generated configuration, executable tools, or media assets.

## Included capabilities

- **Automated game testing** — profile-driven game launch, navigation, capture, and benchmark orchestration.
- **GPU Cooling Tester Evaluation** — controlled cooler test workflows with measurement and reporting support.

## Game and benchmark requirements

The suite and its profile packs do **not** include or redistribute commercial games, benchmark applications, downloadable content, accounts, or license keys. You must install every game or benchmark yourself and have a valid license or other legitimate access to it. A separate purchase is not required when the software is free or is lawfully available through a subscription you already hold.

Using a profile does not bypass a publisher's license, account, anti-cheat, or terms-of-service requirements. Check the relevant software's rules before enabling unattended automation.

## Report template

The app generates self-contained HTML reports from run data. A measurement-free [report template](docs/report-template-example.html) is included to show the structure without presenting synthetic values as hardware conclusions.

## Build

On Windows with the .NET 9 SDK:

```powershell
dotnet build GpuTestSuite.sln -c Release
dotnet test tests/GpuSuite.Tests/GpuSuite.Tests.csproj -c Release -p:Platform=x64
```

Copy `settings.example.json` to `settings.json` and configure only your own local tools and hardware. Do not commit it.

**Elgato Game Capture 4K Pro is required for the full supported automated game-benchmark workflow and is the only capture-card model currently qualified by this project.** Set `captureCardDevice` to the exact DirectShow name `Elgato 4K Pro`. Elgato 4K60 Pro/MK.2 and other cards are unqualified for that workflow, although offline authoring, builds, and diagnostics may still work. See [REQUIREMENTS.txt](REQUIREMENTS.txt); the 60 Hz safety setting is this validated bench path, not a general product limit.

Full pre-flight checks the enabled roster's **PresentMon/RTSS FPS and frametime** requirement separately from the
**FFmpeg + Elgato vision stream** used for OCR/navigation/motion. It confirms the exact qualified device with a
bounded stream probe; no image is retained. The advanced launch override cannot create trustworthy frame evidence or
bypass runtime validation: invalid runs are still rejected.

## PresentMon

PresentMon is not distributed in this repository. Follow [tools/PresentMon/README.md](tools/PresentMon/README.md) to obtain a verified upstream release and place it locally. Its license and attribution are recorded in [NOTICE](NOTICE).

## Profiles

The curated Hardware Busters Verified pack and explicit community samples are listed in [profiles/PROVENANCE.md](profiles/PROVENANCE.md). Community submissions use the review path in [CONTRIBUTING.md](CONTRIBUTING.md).

## Support and governance

Report bugs and security issues through the repository issue tracker as described in [SECURITY.md](SECURITY.md). This repository accepts contributions under [DCO 1.1](DCO.md), follows [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and is governed by [GOVERNANCE.md](GOVERNANCE.md).

Hardware Busters is a trademark/brand name. See [docs/TRADEMARKS-AND-ENTITY.md](docs/TRADEMARKS-AND-ENTITY.md).
