using GpuSuite.Engine.Automation;

namespace GpuSuite.Cli;

internal static partial class Program
{
    /// <summary>
    /// `gpusuite selftest-band` — offline validation of <see cref="GameplayBandTracker"/> (the hitch-tolerant
    /// load→gameplay gate behind WaitForGameplayBand) against synthetic fps sequences, NO game required. Proves
    /// the Ratchet 0/5 fix: a severe-hitch gameplay stream (0-frame stalls + catch-up spikes) now SETTLES, while
    /// a sustained ~60 fps load still NEVER settles (the window can't open mid-load). Exit 0 = all pass.
    /// </summary>
    private static int SelfTestBand()
    {
        Console.WriteLine("\nGameplayBandTracker self-test (hitch-tolerant load→gameplay gate)\n");
        int pass = 0, fail = 0;

        // Feed an fps sequence (one value per 1000 ms poll; 0 = a hard hitch/stall) and return the poll index at
        // which the band first settled, or -1 if it never did. fps == frames when the interval is exactly 1 s.
        static int FirstSettle(GameplayBandTracker t, int[] fpsPerSecond)
        {
            for (int i = 0; i < fpsPerSecond.Length; i++)
                if (t.Feed(fpsPerSecond[i], 1000)) return i;
            return -1;
        }

        // ceiling, floor, sustainMs=4000, bandFraction=0.7 (the Ratchet scene's gate config; floor 0 = one-sided).
        GameplayBandTracker Ratchet() => new(ceiling: 40, floor: 0, sustainMs: 4000, bandFraction: 0.7);

        void Check(string name, bool ok, string detail)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  — {detail}");
            if (ok) pass++; else fail++;
        }

        // 1) Sustained ~60 fps LOAD (above the 40 ceiling) must NEVER settle — the window can't open mid-load.
        {
            var load = Enumerable.Repeat(60, 20).ToArray();
            int s = FirstSettle(Ratchet(), load);
            Check("sustained 60fps load never settles", s < 0, s < 0 ? "never settled (correct)" : $"WRONGLY settled at poll {s}");
        }

        // 2) Clean ~23 fps gameplay settles right after the sustain window fills (~4 polls).
        {
            var gp = Enumerable.Repeat(23, 12).ToArray();
            int s = FirstSettle(Ratchet(), gp);
            Check("clean 23fps gameplay settles", s is >= 3 and <= 5, s < 0 ? "never settled" : $"settled at poll {s}");
        }

        // 3) THE RATCHET FIX: severe-hitch gameplay — mostly ~23 fps with 0-frame STALLS and brief catch-up SPIKES
        //    above the ceiling — must still settle (the old continuous accumulator reset on every hitch → 0/5).
        {
            var hitchy = new[] { 23, 0, 24, 22, 55, 23, 0, 21, 23, 48, 22, 23, 0, 24, 23, 23, 22, 24, 23, 23 };
            int s = FirstSettle(Ratchet(), hitchy);
            Check("hitchy 23fps gameplay (stalls+spikes) settles", s >= 0, s < 0 ? "NEVER settled (Ratchet bug!)" : $"settled at poll {s}");
        }

        // 4) LOAD → GAMEPLAY transition: must NOT settle during the 60 fps load, then settle once gameplay dominates.
        {
            var seq = Enumerable.Repeat(60, 6).Concat(Enumerable.Repeat(23, 10)).ToArray();
            var t = Ratchet();
            int settle = -1; bool settledDuringLoad = false;
            for (int i = 0; i < seq.Length; i++)
            {
                bool ok = t.Feed(seq[i], 1000);
                if (ok && settle < 0) settle = i;
                if (ok && i < 6) settledDuringLoad = true;
            }
            Check("load→gameplay: no mid-load settle, then settles", !settledDuringLoad && settle >= 6,
                  settledDuringLoad ? $"WRONGLY settled mid-load at poll {settle}" : settle < 0 ? "never settled" : $"settled at poll {settle} (gameplay)");
        }

        // 5) Two-sided band (Black Myth style): ceiling 120, floor 30. A ~450 fps menu (above) must not settle; ~60
        //    fps gameplay (in [30,120]) must. Proves the floor still excludes a high-fps menu and a sub-floor load.
        {
            var t = new GameplayBandTracker(ceiling: 120, floor: 30, sustainMs: 4000, bandFraction: 0.7);
            var seq = Enumerable.Repeat(450, 5).Concat(Enumerable.Repeat(60, 8)).ToArray();
            int settle = -1; bool duringMenu = false;
            for (int i = 0; i < seq.Length; i++)
            {
                bool ok = t.Feed(seq[i], 1000);
                if (ok && settle < 0) settle = i;
                if (ok && i < 5) duringMenu = true;
            }
            Check("two-sided band: 450fps menu rejected, 60fps gameplay settles", !duringMenu && settle >= 5,
                  duringMenu ? $"WRONGLY settled on the menu at poll {settle}" : settle < 0 ? "never settled" : $"settled at poll {settle}");
        }

        // 6) Permanent 0-frame stall (frozen / capture dead) must NEVER settle (the real gate's REQUIRED timeout then
        //    aborts → Invalid, rather than a false pass). Neutral polls never fill the window.
        {
            var frozen = Enumerable.Repeat(0, 20).ToArray();
            int s = FirstSettle(Ratchet(), frozen);
            Check("permanent 0-frame stall never settles", s < 0, s < 0 ? "never settled (fail-safe)" : $"WRONGLY settled at poll {s}");
        }

        Console.WriteLine($"\nRESULT: {(fail == 0 ? "PASS" : "FAIL")} — {pass} passed, {fail} failed.");
        return fail == 0 ? 0 : 1;
    }
}
