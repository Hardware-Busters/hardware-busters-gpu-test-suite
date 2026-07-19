using System.Runtime.InteropServices;

namespace GpuSuite.Engine.Display;

/// <summary>A desktop display mode: resolution + vertical refresh.</summary>
public readonly record struct DisplayMode(int Width, int Height, int RefreshHz)
{
    public override string ToString() => $"{Width}x{Height}@{RefreshHz}Hz";
}

/// <summary>
/// Reads and sets the Windows desktop display mode (resolution + refresh) via the Win32
/// ChangeDisplaySettingsEx / EnumDisplaySettings APIs. Two jobs on this bench:
///
///   1. RESOLUTION CONTROL for borderless / desktop-resolution games (Forza, Cyberpunk-borderless,
///      MSFS): set the desktop to the target resolution at the safe refresh before launch, and the
///      game inherits it — giving a real per-resolution matrix without per-game config plumbing.
///
///   2. THE 60 Hz SAFETY CAP. The bench display feeds THROUGH an Elgato 4K Pro capture card that
///      caps at 60 Hz; at any higher refresh the card loses sync and the monitor blanks (NO SIGNAL).
///      <see cref="TrySet"/> therefore REFUSES to set any mode above <paramref name="maxHz"/> — the
///      "never exceed 60 Hz" rule is enforced at the API boundary, so no caller can drive the panel
///      past the cap through this class.
///
/// Robustness note: after a signal drop the driver can momentarily enumerate an EMPTY mode list, so
/// this class never relies on EnumDisplaySettings to FIND the target mode — it CONSTRUCTS a DEVMODE
/// directly (width/height/refresh/bpp) and validates it with CDS_TEST before applying. A mode that
/// fails CDS_TEST is reported as a failure (the caller records an Invalid run) rather than applied —
/// we never push an unvalidated mode that could blank the panel.
/// </summary>
public static class DisplayController
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const uint CDS_UPDATEREGISTRY = 0x00000001;
    private const uint CDS_TEST = 0x00000002;
    private const int DISP_CHANGE_SUCCESSFUL = 0;

    private const uint DM_BITSPERPEL = 0x00040000;
    private const uint DM_PELSWIDTH = 0x00080000;
    private const uint DM_PELSHEIGHT = 0x00100000;
    private const uint DM_DISPLAYFREQUENCY = 0x00400000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);

    private static DEVMODE NewDevmode()
    {
        var dm = new DEVMODE { dmDeviceName = "", dmFormName = "" };
        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODE>();
        return dm;
    }

    /// <summary>
    /// The current desktop mode (resolution + refresh), or null if the driver can't report it (it
    /// transiently enumerates empty right after a signal-loss re-sync). Pass a device name like
    /// "\\.\DISPLAY1" to target a specific output; null = the primary/default display.
    /// </summary>
    public static DisplayMode? GetCurrent(string? device = null)
    {
        var dm = NewDevmode();
        if (EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) != 0 && dm.dmPelsWidth > 0)
            return new DisplayMode((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);
        return null;
    }

    /// <summary>
    /// Validate (CDS_TEST) whether a mode is settable WITHOUT applying it. Honors the same
    /// <paramref name="maxHz"/> safety cap as <see cref="TrySet"/> — a mode above the cap is reported
    /// not-settable even if the panel supports it.
    /// </summary>
    public static bool CanSet(int width, int height, int hz, int maxHz = 60, string? device = null)
    {
        if (hz > maxHz) return false;
        return Apply(width, height, hz, test: true, device, out _);
    }

    /// <summary>
    /// Set the desktop to <paramref name="width"/>x<paramref name="height"/> @ <paramref name="hz"/>.
    /// REFUSES any <paramref name="hz"/> above <paramref name="maxHz"/> (the Elgato 60 Hz cap — higher
    /// blanks the panel), validates the constructed mode with CDS_TEST first, and only then commits it
    /// (CDS_UPDATEREGISTRY). Returns (false, reason) without touching the display if the cap is exceeded
    /// or the mode is not settable, so a caller never blanks the screen on a bad request.
    /// </summary>
    public static (bool ok, string detail) TrySet(int width, int height, int hz, int maxHz = 60, string? device = null)
    {
        if (hz > maxHz)
            return (false, $"refused: {hz}Hz exceeds the {maxHz}Hz display cap (Elgato sync limit) — NOT set");
        if (!Apply(width, height, hz, test: true, device, out int testCode))
            return (false, $"mode {width}x{height}@{hz}Hz not settable on this display (CDS_TEST={testCode})");
        if (!Apply(width, height, hz, test: false, device, out int setCode))
            return (false, $"mode {width}x{height}@{hz}Hz validated but apply failed (ChangeDisplaySettingsEx={setCode})");
        return (true, $"{width}x{height}@{hz}Hz");
    }

    /// <summary>
    /// Set the desktop to a target resolution at the safe cap refresh (<paramref name="maxHz"/>, the
    /// bench's 60 Hz). The borderless-game path: do this before launch and the game renders at this
    /// resolution and refresh. Returns (false, reason) without blanking if that resolution has no
    /// mode at the cap refresh.
    /// </summary>
    public static (bool ok, string detail) TrySetResolutionAtCap(int width, int height, int maxHz = 60, string? device = null)
        => TrySet(width, height, maxHz, maxHz, device);

    private static bool Apply(int width, int height, int hz, bool test, string? device, out int code)
    {
        var dm = NewDevmode();
        dm.dmPelsWidth = (uint)width;
        dm.dmPelsHeight = (uint)height;
        dm.dmDisplayFrequency = (uint)hz;
        dm.dmBitsPerPel = 32;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        code = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, test ? CDS_TEST : CDS_UPDATEREGISTRY, IntPtr.Zero);
        return code == DISP_CHANGE_SUCCESSFUL;
    }
}
