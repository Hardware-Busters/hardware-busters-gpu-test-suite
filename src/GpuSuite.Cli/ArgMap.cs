namespace GpuSuite.Cli;

/// <summary>Tiny argument parser: --key value or --flag.</summary>
internal sealed class ArgMap
{
    private readonly string[] _a;
    public ArgMap(string[] a) => _a = a;

    public bool Has(string name) => _a.Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));

    public string? Get(string name)
    {
        for (int i = 0; i < _a.Length - 1; i++)
            if (string.Equals(_a[i], name, StringComparison.OrdinalIgnoreCase)) return _a[i + 1];
        return null;
    }

    public int? GetInt(string name) => int.TryParse(Get(name), out var v) ? v : null;

    public double? GetDouble(string name) =>
        double.TryParse(Get(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
}
