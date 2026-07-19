# Public installer release runbook

Tag only a reviewed public `main` commit. The tagged workflow builds source on a clean Windows runner, creates the one user-facing installer asset `HardwareBustersGpuTestSuite-Setup.exe`, verifies its SHA-256, silently installs it into a temporary folder, and checks that `GpuSuite.App.exe` exists there.

The installer is a GitHub Release asset, never a tracked repository file. The release contains the installer, its SHA-256 file, and SBOM metadata only. It must not download from a private source or include local results, settings, profiles outside the committed public pack, or external private tooling.
