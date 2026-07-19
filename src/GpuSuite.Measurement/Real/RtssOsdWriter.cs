using System.IO.MemoryMappedFiles;
using System.Text;

namespace GpuSuite.Measurement.Real;

/// <summary>
/// Writes a custom multi-line text block to the RivaTuner (RTSS) On-Screen Display via its shared
/// memory ("RTSSSharedMemoryV2"). RTSS renders the text on top of any hooked 3D app, so during a
/// benchmark the live FPS / power / temperatures / test-pass overlay appears over the game itself —
/// no separate overlay window, and it composites in the game's swap-chain exactly like RTSS's own OSD.
///
/// Mechanism (per RTSS SDK Include/RTSSSharedMemory.h + RTSSSharedMemorySampleDlg.cpp::UpdateOSD):
/// claim a slot in the OSD array by writing our name into szOSDOwner, write text into szOSD, then
/// bump the header's dwOSDFrame counter so RTSS repaints. Requires RTSS to be running and hooking the
/// target (the factory's EnsureRtssRunning handles launch). Field NAMES are authoritative; the App/OSD
/// comments in the header are famously swapped, so we index by the dwOSD* fields as the SDK sample does.
/// </summary>
public sealed class RtssOsdWriter : IDisposable
{
    public const string SharedMemoryName = "RTSSSharedMemoryV2";
    private const uint Signature = 0x52545353;   // 'RTSS'
    private const uint MinVersion = 0x00020000;  // v2.0 layout

    // RTSS_SHARED_MEMORY header (bytes). The App* fields (8/12/16) are shared with RtssFrameProvider;
    // the OSD* fields follow them: dwOSDEntrySize, dwOSDArrOffset, dwOSDArrSize, dwOSDFrame.
    private const int H_SIGNATURE = 0, H_VERSION = 4;
    private const int H_OSD_ENTRY_SIZE = 20, H_OSD_ARR_OFFSET = 24, H_OSD_ARR_SIZE = 28, H_OSD_FRAME = 32;
    // RTSS_SHARED_MEMORY_OSD_ENTRY: szOSD[256] @0, szOSDOwner[256] @256, szOSDEx[4096] @512.
    private const int O_TEXT = 0, O_OWNER = 256, O_TEXT_EX = 512;
    private const int TextMax = 255, OwnerMax = 255;

    private readonly string _owner;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _acc;
    private long _slotBase = -1;
    private uint _osdEntrySize;
    private bool _disposed;

    public RtssOsdWriter(string owner = "GpuTestSuite") => _owner = owner;

    /// <summary>True once a writable OSD slot is claimed. Call before Update/Clear.</summary>
    public bool IsOpen => _acc is not null && _slotBase >= 0;

    /// <summary>
    /// Open the shared memory for read/write and claim an OSD slot (reusing ours if present, else the
    /// first free one). Returns false — without throwing — if RTSS isn't running, the layout is
    /// unexpected, or the mapping can't be opened for write (e.g. ACL); the OSD then stays disabled.
    /// </summary>
    public bool Open()
    {
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(SharedMemoryName, MemoryMappedFileRights.ReadWrite);
            _acc = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            if (_acc.ReadUInt32(H_SIGNATURE) != Signature || _acc.ReadUInt32(H_VERSION) < MinVersion) { Dispose(); return false; }

            _osdEntrySize = _acc.ReadUInt32(H_OSD_ENTRY_SIZE);
            uint arrOffset = _acc.ReadUInt32(H_OSD_ARR_OFFSET);
            uint arrSize = _acc.ReadUInt32(H_OSD_ARR_SIZE);
            if (_osdEntrySize == 0 || arrOffset == 0 || arrSize == 0) { Dispose(); return false; }

            _slotBase = ClaimSlot(arrOffset, arrSize);
            if (_slotBase < 0) { Dispose(); return false; }
            return true;
        }
        catch { Dispose(); return false; }
    }

    /// <summary>Reuse the slot we already own (so re-opening across runs doesn't leak slots), else
    /// claim the first free slot by stamping our owner name into it.</summary>
    private long ClaimSlot(uint arrOffset, uint arrSize)
    {
        long firstFree = -1;
        for (uint i = 0; i < arrSize; i++)
        {
            long b = arrOffset + (long)i * _osdEntrySize;
            string owner = ReadAscii(b + O_OWNER, OwnerMax);
            if (string.Equals(owner, _owner, StringComparison.Ordinal)) return b;       // already ours
            if (firstFree < 0 && owner.Length == 0) firstFree = b;                       // remember first empty
        }
        if (firstFree >= 0) WriteAscii(firstFree + O_OWNER, _owner, OwnerMax);
        return firstFree;
    }

    /// <summary>Replace the OSD text for our slot and signal RTSS to repaint. Multi-line via '\n'.</summary>
    public void Update(string text)
    {
        if (!IsOpen) return;
        WriteAscii(_slotBase + O_TEXT, text ?? "", TextMax);
        // Keep the extended buffer empty so RTSS renders our szOSD text unambiguously (some builds
        // prefer szOSDEx when it is non-empty — we always drive the classic, universally-rendered field).
        if (_osdEntrySize > O_TEXT_EX) _acc!.Write(_slotBase + O_TEXT_EX, (byte)0);
        BumpFrame();
    }

    /// <summary>Blank our OSD line (leaves the slot owned, ready for the next Update).</summary>
    public void Clear()
    {
        if (!IsOpen) return;
        _acc!.Write(_slotBase + O_TEXT, (byte)0);
        BumpFrame();
    }

    private void BumpFrame()
    {
        try { uint f = _acc!.ReadUInt32(H_OSD_FRAME); _acc.Write(H_OSD_FRAME, f + 1); } catch { }
    }

    private string ReadAscii(long offset, int max)
    {
        Span<byte> buf = stackalloc byte[max + 1];
        for (int i = 0; i <= max; i++) buf[i] = _acc!.ReadByte(offset + i);
        int len = buf.IndexOf((byte)0); if (len < 0) len = max;
        return Encoding.ASCII.GetString(buf[..len]);
    }

    private void WriteAscii(long offset, string text, int max)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        int n = Math.Min(bytes.Length, max);
        for (int i = 0; i < n; i++) _acc!.Write(offset + i, bytes[i]);
        _acc!.Write(offset + n, (byte)0); // null-terminate
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Release the slot on the way out: blank the text AND free the owner so RTSS stops drawing it
        // and the slot can be reused. Best-effort — never throw from Dispose.
        try
        {
            if (_acc is not null && _slotBase >= 0)
            {
                _acc.Write(_slotBase + O_TEXT, (byte)0);
                _acc.Write(_slotBase + O_OWNER, (byte)0);
                BumpFrame();
            }
        }
        catch { }
        _acc?.Dispose();
        _mmf?.Dispose();
        _acc = null; _mmf = null; _slotBase = -1;
    }
}
