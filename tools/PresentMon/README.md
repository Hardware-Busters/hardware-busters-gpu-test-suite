# PresentMon bootstrap

This source repository does not commit PresentMon binaries. The published installer includes the official standalone x64 PresentMon 2.6.0 console executable from the [upstream v2.6.0 release](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0). Release CI verifies its published SHA-256 (`b2a706bc6ad475749e3b7e3409263aa1e6906d45bdcf993f6dbc0f660188f1af`) before packaging, records it in the SBOM, and includes the upstream MIT license in `LICENSES/PresentMon-MIT.txt`.

For a source checkout, download the same upstream executable, verify its release provenance and SHA-256, and place it at `tools/PresentMon/PresentMon.exe`. Do not commit the executable. `Install-PresentMon.ps1` is a guarded local placement helper; release CI uses `Stage-VerifiedPresentMon.ps1`. Upstream attribution is recorded in [NOTICE](../../NOTICE).
