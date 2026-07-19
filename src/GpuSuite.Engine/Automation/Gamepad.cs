using GpuSuite.Core.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Exceptions;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace GpuSuite.Engine.Automation;

/// <summary>
/// A virtual Xbox 360 gamepad the bot engine can drive. RE Engine games (Resident Evil 4, etc.)
/// FILTER injected keyboard/mouse (SendInput is ignored), but accept a ViGEm virtual pad because it
/// presents to the game as REAL HID gamepad hardware. Two implementations:
/// <see cref="ViGemGamepad"/> (real — needs the one-time ViGEmBus kernel driver) and
/// <see cref="NullGamepad"/> (no-op fallback when the driver is absent, so a run degrades to a
/// logged dry-run instead of crashing).
///
/// This interface is the ONLY thing the engine depends on; the Nefarius.ViGEm.Client package is
/// referenced solely inside this file, so the dependency's blast radius is contained and swappable.
/// </summary>
public interface IGamepad : IDisposable
{
    /// <summary>True only when a virtual pad was actually created (ViGEmBus present + pad connected).</summary>
    bool Connected { get; }
    /// <summary>Human-readable reason the pad is unavailable (for logging), or null when Connected.</summary>
    string? Unavailable { get; }
    /// <summary>Press/release a face/shoulder/dpad/stick/menu button by name (A,B,X,Y,LB,RB,Start,Back,Up,…).</summary>
    void Button(string name, bool pressed);
    /// <summary>Left stick deflection, each axis -1..1 (x: left..right, y: down..up). Persists until changed.</summary>
    void LeftStick(double x, double y);
    /// <summary>Right stick deflection, each axis -1..1 (x: left..right, y: down..up). Persists until changed.</summary>
    void RightStick(double x, double y);
    /// <summary>Trigger pull 0..1. side = "left"/"right" (aliases lt/rt/l2/r2).</summary>
    void Trigger(string side, double value);
    /// <summary>Center both sticks, release all buttons, zero both triggers.</summary>
    void Neutral();
}

/// <summary>Factory: a real ViGEm pad if the driver is installed, else a logged no-op pad.</summary>
public static class Gamepad
{
    public static IGamepad Create(RunLogger log)
    {
        try
        {
            var pad = new ViGemGamepad();
            log.Info("Pad", "ViGEm virtual Xbox 360 controller connected (presents as real gamepad hardware).");
            return pad;
        }
        catch (VigemBusNotFoundException)
        {
            const string why = "ViGEmBus driver not installed";
            log.Warn("Pad", $"Virtual gamepad unavailable ({why}). Install ViGEmBus, then verify with 'gpusuite vigem-check'.");
            return new NullGamepad(why);
        }
        catch (Exception ex)
        {
            string why = ex.GetType().Name + ": " + ex.Message;
            log.Warn("Pad", $"Virtual gamepad could not be created ({why}). See 'gpusuite vigem-check'.");
            return new NullGamepad(why);
        }
    }
}

/// <summary>No-op pad used when ViGEmBus is absent — every call is ignored so the run degrades to a dry-run.</summary>
public sealed class NullGamepad : IGamepad
{
    public NullGamepad(string why) => Unavailable = why;
    public bool Connected => false;
    public string? Unavailable { get; }
    public void Button(string name, bool pressed) { }
    public void LeftStick(double x, double y) { }
    public void RightStick(double x, double y) { }
    public void Trigger(string side, double value) { }
    public void Neutral() { }
    public void Dispose() { }
}

/// <summary>Real virtual Xbox 360 controller backed by ViGEmBus. Auto-submits a report on every change.</summary>
public sealed class ViGemGamepad : IGamepad
{
    private readonly ViGEmClient _client;
    private readonly IXbox360Controller _pad;

    /// <summary>Throws <see cref="VigemBusNotFoundException"/> when the ViGEmBus driver is not installed.</summary>
    public ViGemGamepad()
    {
        var client = new ViGEmClient();          // throws if the bus driver is missing
        try
        {
            var pad = client.CreateXbox360Controller();
            pad.AutoSubmitReport = true;          // every Set* call pushes a HID report immediately
            pad.Connect();
            _client = client;
            _pad = pad;
        }
        catch { client.Dispose(); throw; }
    }

    public bool Connected => true;
    public string? Unavailable => null;

    public void Button(string name, bool pressed)
    {
        var b = MapButton(name);
        if (b is not null) _pad.SetButtonState(b, pressed);
    }

    public void LeftStick(double x, double y)
    {
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxis(x));
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxis(y));
    }

    public void RightStick(double x, double y)
    {
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, ToAxis(x));
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, ToAxis(y));
    }

    public void Trigger(string side, double value)
    {
        byte v = (byte)Math.Clamp((int)Math.Round(Math.Clamp(value, 0, 1) * 255), 0, 255);
        bool right = side.Trim().ToLowerInvariant() is "right" or "r" or "rt" or "r2";
        _pad.SetSliderValue(right ? Xbox360Slider.RightTrigger : Xbox360Slider.LeftTrigger, v);
    }

    public void Neutral()
    {
        LeftStick(0, 0);
        RightStick(0, 0);
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, 0);
        foreach (var b in AllButtons) _pad.SetButtonState(b, false);
    }

    public void Dispose()
    {
        try { Neutral(); _pad.Disconnect(); } catch { /* best-effort */ }
        try { _client.Dispose(); } catch { /* best-effort */ }
    }

    // -1..1 -> short axis range (centered at 0). Clamp guards out-of-range script values.
    private static short ToAxis(double v) => (short)Math.Clamp((int)Math.Round(Math.Clamp(v, -1, 1) * 32767), short.MinValue, short.MaxValue);

    private static Xbox360Button? MapButton(string name) => name.Trim().ToUpperInvariant() switch
    {
        "A" => Xbox360Button.A, "B" => Xbox360Button.B, "X" => Xbox360Button.X, "Y" => Xbox360Button.Y,
        "LB" or "LEFTSHOULDER" or "L1" => Xbox360Button.LeftShoulder,
        "RB" or "RIGHTSHOULDER" or "R1" => Xbox360Button.RightShoulder,
        "LS" or "LEFTTHUMB" or "L3" => Xbox360Button.LeftThumb,
        "RS" or "RIGHTTHUMB" or "R3" => Xbox360Button.RightThumb,
        "START" or "MENU" => Xbox360Button.Start,
        "BACK" or "VIEW" or "SELECT" => Xbox360Button.Back,
        "GUIDE" or "HOME" => Xbox360Button.Guide,
        "UP" or "DPADUP" => Xbox360Button.Up,
        "DOWN" or "DPADDOWN" => Xbox360Button.Down,
        "LEFT" or "DPADLEFT" => Xbox360Button.Left,
        "RIGHT" or "DPADRIGHT" => Xbox360Button.Right,
        _ => null
    };

    private static readonly Xbox360Button[] AllButtons =
    {
        Xbox360Button.A, Xbox360Button.B, Xbox360Button.X, Xbox360Button.Y,
        Xbox360Button.LeftShoulder, Xbox360Button.RightShoulder, Xbox360Button.LeftThumb, Xbox360Button.RightThumb,
        Xbox360Button.Start, Xbox360Button.Back, Xbox360Button.Guide,
        Xbox360Button.Up, Xbox360Button.Down, Xbox360Button.Left, Xbox360Button.Right
    };
}
