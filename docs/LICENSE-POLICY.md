# License policy

| Material | License or handling |
| --- | --- |
| Engine, application, CLI, test, and functional build source | Apache-2.0 |
| Explicit public sample profiles and provenance manifest | CC-BY-4.0 with retained attribution |
| Third-party NuGet dependencies | Their upstream terms; review before dependency updates |
| PresentMon 2.6.0 standalone x64 executable | Source repository remains binary-free; the release installer contains the official upstream binary after exact SHA-256 verification, under MIT with `LICENSES/PresentMon-MIT.txt` retained in the installer |
| Powenetics protocol source in `src/GpuSuite.Measurement/Real/Powenetics/` and `src/GpuSuite.Measurement/Real/PoweneticsPowerProvider.cs` | Apache-2.0 by express Cybenetics LTD authorization for this public release |
| Powenetics hardware, firmware, device binaries, documentation, and marks | Not licensed by this repository |
| Hardware Busters marks | Permission required except nominative reference |

Do not add a binary, generated game configuration, result, secret, private operational document, or third-party asset without an explicit review and license record in `NOTICE`.

Each installer release generates `audit/sbom.cdx.json` and `THIRD-PARTY-NOTICES.md` from the restored NuGet graph and the staged, checksum-verified PresentMon binary. The notices and PresentMon MIT license are staged into the installer; the SBOM is released with it.
