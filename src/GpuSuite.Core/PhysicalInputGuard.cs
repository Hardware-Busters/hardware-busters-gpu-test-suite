using System.Runtime.InteropServices;

namespace GpuSuite.Core;

/// <summary>
/// In-process physical keyboard+mouse lock for unattended benchmark runs.
///
/// While armed, every <b>physical</b> key and mouse event is swallowed so stray human input
/// can't pollute the bots' menu navigation or the frame-time measurement window. <b>Injected</b>
/// input passes straight through — the bots drive games via <c>SendInput</c>, which carries the
/// <c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c> flag, and the ViGEm virtual gamepad sits below
/// the low-level hook entirely — so automation is completely unaffected. The suite's own tool
/// commands spawn processes (no synthetic input) and are likewise untouched.
///
/// Physical <b>ESC</b> is the single abort: it fires the supplied callback (a graceful
/// <c>CancellationTokenSource.Cancel</c>) and is itself swallowed so it never leaks to the game.
/// It does NOT hard-kill anything — the run unwinds cleanly through the normal cancellation path.
///
/// The WH_KEYBOARD_LL / WH_MOUSE_LL hooks live on a dedicated background thread with its own
/// message pump, because low-level hooks require the installing thread to keep pumping messages
/// or the OS evicts them (LowLevelHooksTimeout). <see cref="Dispose"/> tears the hooks down; two
/// safety nets guarantee the user is never left locked out: a watchdog self-unlocks if an abort
/// isn't honoured within <c>autoUnlockAfterAbortMs</c> (orchestrator slow to cancel), and a panic
/// escape force-unlocks immediately on three physical ESC presses. Killing the process also frees
/// input — the OS removes low-level hooks when their owning process dies. Ctrl+Alt+Del is the
/// OS-level fallback that no hook can intercept.
/// </summary>
public sealed class PhysicalInputGuard : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012;
    private const uint WM_TIMER = 0x0113;
    private const uint LLKHF_INJECTED = 0x10;
    private const uint LLMHF_INJECTED = 0x01;
    private const int VK_ESCAPE = 0x1B;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT { public int x; public int y; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private readonly Action _onAbort;
    private readonly Action<string>? _log;
    private readonly int _autoUnlockAfterAbortMs;
    private readonly Func<bool>? _isHung;    // pump-thread polled: true ⇒ game crashed/hung ⇒ auto-release
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    // Keep the delegates rooted for the lifetime of the hooks — if they're collected,
    // the native callback jumps into freed memory and the process dies.
    private HookProc? _kbProc, _msProc;
    private IntPtr _kbHook = IntPtr.Zero, _msHook = IntPtr.Zero;
    private uint _threadId;
    private volatile bool _disposed;
    private bool _abortFired;        // pump-thread only
    private long _abortTick;         // pump-thread only (TickCount64 at first ESC; 0 = none)
    private int _escCount;           // pump-thread only

    // Diagnostics — written on the pump thread, read after Join (safe via the join barrier).
    public int BlockedKeys;
    public int BlockedMouse;

    private PhysicalInputGuard(Action onAbort, Action<string>? log, int autoUnlockAfterAbortMs, Func<bool>? isHung)
    {
        _onAbort = onAbort;
        _log = log;
        _autoUnlockAfterAbortMs = autoUnlockAfterAbortMs;
        _isHung = isHung;
        _thread = new Thread(PumpThread) { IsBackground = true, Name = "InputGuard" };
    }

    /// <summary>
    /// Install the lock and return once the hooks are in place (or installation has failed).
    /// Inspect <see cref="IsArmed"/> to confirm; a failed arm is a no-op the caller can ignore.
    /// <paramref name="isHung"/>, when supplied, is polled once a second on the hook thread: when it
    /// returns true (the run's progress beacon has gone stale — the game crashed or hung), the guard
    /// fires the graceful abort itself and then force-unlocks if it isn't honoured, so the operator
    /// regains the keyboard and mouse without waiting for the whole suite to die.
    /// </summary>
    public static PhysicalInputGuard Arm(Action onAbort, Action<string>? log = null, int autoUnlockAfterAbortMs = 8000, Func<bool>? isHung = null)
    {
        var g = new PhysicalInputGuard(onAbort, log, autoUnlockAfterAbortMs, isHung);
        g._thread.Start();
        g._ready.Wait(3000);   // block until the pump thread has called SetWindowsHookEx
        return g;
    }

    /// <summary>True when at least one low-level hook is installed.</summary>
    public bool IsArmed => _kbHook != IntPtr.Zero || _msHook != IntPtr.Zero;

    private void PumpThread()
    {
        _threadId = GetCurrentThreadId();
        _kbProc = KbCallback;
        _msProc = MsCallback;
        IntPtr hMod = GetModuleHandle(null);
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, hMod, 0);
        _msHook = SetWindowsHookEx(WH_MOUSE_LL, _msProc, hMod, 0);
        SetTimer(IntPtr.Zero, UIntPtr.Zero, 1000, IntPtr.Zero);   // 1 s watchdog tick (thread message)
        _ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) is var r and not 0)
        {
            if (r == -1) break;   // GetMessage error
            if (msg.message == WM_TIMER)
            {
                // Crash/hang net: if the run's progress beacon has gone stale — no frames presented and
                // the game never reached the captured display (the "no main window yet / capture shows the
                // desktop" loop) — no ESC is coming, so fire the graceful abort ourselves. This is the case
                // a plain ESC-only lock can't cover: the GAME died/hung while the SUITE kept the hooks alive.
                if (_abortTick == 0 && _isHung is not null)
                {
                    bool hung; try { hung = _isHung(); } catch { hung = false; }
                    if (hung) BeginAbort("game crashed or hung (no render/nav progress) — auto-releasing the input lock.");
                }
                // Safety net: if an abort was requested (ESC or the crash/hang net above) but Dispose()
                // hasn't torn us down within the grace window (orchestrator slow to honour the cancel),
                // self-unlock so the physical user is never stuck behind the lock.
                if (_abortTick != 0 && Environment.TickCount64 - _abortTick > _autoUnlockAfterAbortMs)
                {
                    _log?.Invoke($"abort not honoured in {_autoUnlockAfterAbortMs} ms — self-unlocking input.");
                    break;
                }
                continue;
            }
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        Unhook();
    }

    private IntPtr KbCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & LLKHF_INJECTED) != 0;
            if (!injected)
            {
                if (k.vkCode == VK_ESCAPE)
                {
                    FireAbort();
                    return (IntPtr)1;          // swallow ESC — abort only; never leak it to the game
                }
                BlockedKeys++;
                return (IntPtr)1;              // swallow every other physical key
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);   // injected (bot) -> pass through
    }

    private IntPtr MsCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            bool injected = (m.flags & LLMHF_INJECTED) != 0;
            if (!injected)
            {
                BlockedMouse++;
                return (IntPtr)1;              // swallow all physical mouse (move, click, wheel)
            }
        }
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private void FireAbort()
    {
        int n = ++_escCount;
        BeginAbort("ESC — aborting run (graceful cancel).");
        if (n >= 3)
        {
            // Panic escape — three physical ESC presses force an immediate unlock even if the
            // graceful cancel is wedged.
            _log?.Invoke("ESC x3 — force-unlocking input now.");
            PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Start a graceful abort and the force-unlock grace clock. Idempotent and pump-thread only;
    /// shared by the ESC abort and the crash/hang watchdog.</summary>
    private void BeginAbort(string reason)
    {
        if (_abortTick == 0) _abortTick = Environment.TickCount64;
        if (_abortFired) return;
        _abortFired = true;
        _log?.Invoke(reason);
        // Run the cancel off the hook thread: low-level callbacks must return fast, and
        // CancellationTokenSource.Cancel can run continuations synchronously.
        ThreadPool.QueueUserWorkItem(_ => { try { _onAbort(); } catch { /* best-effort abort */ } });
    }

    private void Unhook()
    {
        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(3000);
        Unhook();   // belt-and-suspenders if the pump thread never ran the loop
        _ready.Dispose();
    }
}
