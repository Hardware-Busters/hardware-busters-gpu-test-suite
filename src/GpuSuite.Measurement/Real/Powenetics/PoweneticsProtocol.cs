using GpuSuite.Core.Models;

namespace GpuSuite.Measurement.Real.Powenetics;

/// <summary>
/// Powenetics V2 (CWT PMD) wire protocol — ported verbatim from the proven Powenetics app
/// (PMD_Operations.cs / PMD_Codes.cs). Pure, allocation-light, and unit-testable offline so
/// the decode can be validated WITHOUT the device attached.
///
/// Wire format: 921600 8N1. Each frame = sync [0xCA 0xAC] + 67-byte payload.
/// Payload = 2-byte packet number (big-endian) + 13 channels × 5 bytes.
/// Per channel: voltage = (b0*256 + b1)/1000 V ; current = (b2*65536 + b3*256 + b4)/1000 A.
/// Power per rail = V*I, gated to V &gt; 1 to reject noise (matches the proven logger).
/// </summary>
public static class PoweneticsProtocol
{
    public const byte Sync1 = 0xCA;
    public const byte Sync2 = 0xAC;
    public const int PayloadLength = 67;
    public const int FrameLength = 2 + PayloadLength;

    // Init/handshake commands (from PMDCommands).
    public static readonly byte[] CalibrationOk = { 0xCA, 0xAC, 0xBD, 0x01 };
    public static readonly byte[] StreamMode = { 0xCA, 0xAC, 0xBD, 0x90 };

    /// <summary>Channel index → rail (0-based channel, matching the proven "Channel N" comments).</summary>
    // 0:3.3V ATX  1:5VSB  2:12V ATX  3:5V ATX  4:EPS1(12V1)  5:12Vsb(10pin)
    // 6:EPS3(12V3) 7:EPS2(12V2) 8:PCIe#3(12V6) 9:PCIe#2(12V5) 10:Slot3.3V(OPTI) 11:Slot12V(OPTI) 12:PCIe#1(12V4)

    public static int PacketNumber(ReadOnlySpan<byte> payload) => payload[0] * 256 + payload[1];

    /// <summary>Decode one 67-byte payload into a <see cref="PowerSample"/> at the given time.</summary>
    public static PowerSample Decode(ReadOnlySpan<byte> payload, double timeSec)
    {
        Span<double> p = stackalloc double[13]; // power per channel
        for (int c = 0; c < 13; c++)
        {
            int off = 2 + c * 5;
            double v = (payload[off] * 256 + payload[off + 1]) / 1000.0;
            double a = (payload[off + 2] * 65536 + payload[off + 3] * 256 + payload[off + 4]) / 1000.0;
            p[c] = v > 1.0 ? v * a : 0.0;
        }

        double slot12 = p[11], slot33 = p[10];
        double pcie1 = p[12], pcie2 = p[9], pcie3 = p[8];
        double eps1 = p[4], eps2 = p[7], eps3 = p[6];
        double atx12 = p[2], atx5 = p[3], atx33 = p[0], atxStb = p[1];

        double pcieSlotTotal = slot12 + slot33;
        double pcieConnTotal = pcie1 + pcie2 + pcie3;
        double gpuTotal = pcieSlotTotal + pcieConnTotal;
        double epsAll = eps1 + eps2 + eps3;
        double atxAll = atx12 + atx5 + atx33 + atxStb;
        double cpuPmd = (atxAll + epsAll) - slot12 - (atx5 + atx33) - atxStb;
        double total = atxAll + epsAll + gpuTotal;

        return new PowerSample
        {
            TimeSec = timeSec,
            GpuTotalW = Round4(gpuTotal),
            PcieSlot12vW = Round4(slot12),
            PcieSlot3v3W = Round4(slot33),
            Pcie8pin1W = Round4(pcie1),
            Pcie8pin2W = Round4(pcie2),
            Pcie8pin3W = Round4(pcie3),
            CpuTotalW = Round4(cpuPmd),
            Eps1W = Round4(eps1),
            Eps2W = Round4(eps2),
            Atx12vW = Round4(atx12),
            SystemTotalW = Round4(total)
        };
    }

    private static double Round4(double v) => Math.Round(v, 4);

    /// <summary>True if a decoded sample looks physically plausible (sanity gate from the proven logger).</summary>
    public static bool IsPlausible(PowerSample s) =>
        s.GpuTotalW is >= 0 and < 5000 && s.SystemTotalW is >= 0 and < 6000;

    // ---- Test helper: build a raw frame from per-channel (V, A) pairs ----
    /// <summary>Encode a full 69-byte frame (sync + payload) for offline decoder self-tests.</summary>
    public static byte[] EncodeFrame(int packetNumber, (double v, double a)[] channels13)
    {
        if (channels13.Length != 13) throw new ArgumentException("Expected 13 channels.");
        var frame = new byte[FrameLength];
        frame[0] = Sync1; frame[1] = Sync2;
        frame[2] = (byte)(packetNumber >> 8);
        frame[3] = (byte)(packetNumber & 0xFF);
        for (int c = 0; c < 13; c++)
        {
            int off = 2 + 2 + c * 5; // +2 sync, +2 packet number
            int mv = (int)Math.Round(channels13[c].v * 1000);
            int ma = (int)Math.Round(channels13[c].a * 1000);
            frame[off + 0] = (byte)((mv >> 8) & 0xFF);
            frame[off + 1] = (byte)(mv & 0xFF);
            frame[off + 2] = (byte)((ma >> 16) & 0xFF);
            frame[off + 3] = (byte)((ma >> 8) & 0xFF);
            frame[off + 4] = (byte)(ma & 0xFF);
        }
        return frame;
    }
}

/// <summary>
/// Reassembles a byte stream into 67-byte payloads, mirroring the proven app's resync logic:
/// finds the [CA AC] sync, waits for a full frame, validates the sync, emits the payload.
/// </summary>
public sealed class PoweneticsFrameAssembler
{
    private readonly List<byte> _buf = new();
    private bool _synced;

    public long FramesParsed { get; private set; }
    public long MalformedDropped { get; private set; }

    public IEnumerable<byte[]> Push(byte[] bytes)
    {
        _buf.AddRange(bytes);
        while (true)
        {
            if (!_synced)
            {
                int idx = FindSync(_buf);
                if (idx < 0)
                {
                    if (_buf.Count > 1)
                    {
                        MalformedDropped++;
                        byte last = _buf[^1];
                        _buf.Clear();
                        if (last == PoweneticsProtocol.Sync1) _buf.Add(last);
                    }
                    yield break;
                }
                if (idx > 0) { MalformedDropped++; _buf.RemoveRange(0, idx); }
                _synced = true;
            }

            if (_buf.Count < PoweneticsProtocol.FrameLength) yield break;

            if (_buf[0] != PoweneticsProtocol.Sync1 || _buf[1] != PoweneticsProtocol.Sync2)
            {
                MalformedDropped++; _synced = false; continue;
            }

            var payload = new byte[PoweneticsProtocol.PayloadLength];
            _buf.CopyTo(2, payload, 0, PoweneticsProtocol.PayloadLength);
            _buf.RemoveRange(0, PoweneticsProtocol.FrameLength);
            FramesParsed++;
            yield return payload;
        }
    }

    private static int FindSync(List<byte> b)
    {
        for (int i = 0; i < b.Count - 1; i++)
            if (b[i] == PoweneticsProtocol.Sync1 && b[i + 1] == PoweneticsProtocol.Sync2) return i;
        return -1;
    }
}
