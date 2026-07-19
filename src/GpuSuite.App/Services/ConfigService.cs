using GpuSuite.Core.Config;
using GpuSuite.Core.Io;

namespace GpuSuite.App.Services;

/// <summary>
/// Loads/saves the suite's <see cref="SuiteConfig"/> (settings.json) via the same
/// <see cref="Json"/> helper the CLI uses (see Program.LoadConfig). Missing file ⇒ defaults.
/// </summary>
public sealed class ConfigService
{
    public SuiteConfig Load(string path) => Json.Load<SuiteConfig>(path) ?? new SuiteConfig();

    public void Save(string path, SuiteConfig cfg) => Json.Save(path, cfg);
}
