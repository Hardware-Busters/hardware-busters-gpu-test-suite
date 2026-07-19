# Hardware Busters GPU Test Suite

An open, reproducible .NET framework for GPU benchmark orchestration, measurement provenance, reporting, and profile authoring.

This public source release contains code, safe defaults, a curated Hardware Busters Verified game pack, and disabled community samples. It does not contain benchmark results, machine settings, private operations material, game-generated configuration, executable tools, or media assets.

## Build

On Windows with the .NET 9 SDK:

```powershell
dotnet build GpuTestSuite.sln -c Release
dotnet test tests/GpuSuite.Tests/GpuSuite.Tests.csproj -c Release -p:Platform=x64
```

Copy `settings.example.json` to `settings.json` and configure only your own local tools and hardware. Do not commit it.

## PresentMon

PresentMon is not distributed in this repository. Follow [tools/PresentMon/README.md](tools/PresentMon/README.md) to obtain a verified upstream release and place it locally. Its license and attribution are recorded in [NOTICE](NOTICE).

## Profiles

The curated Hardware Busters Verified pack and explicit community samples are listed in [profiles/PROVENANCE.md](profiles/PROVENANCE.md). Community submissions use the review path in [CONTRIBUTING.md](CONTRIBUTING.md).

## Support and governance

Report bugs and security issues through the repository issue tracker as described in [SECURITY.md](SECURITY.md). This repository accepts contributions under [DCO 1.1](DCO.md), follows [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md), and is governed by [GOVERNANCE.md](GOVERNANCE.md).

Hardware Busters is a trademark/brand name. See [docs/TRADEMARKS-AND-ENTITY.md](docs/TRADEMARKS-AND-ENTITY.md).
