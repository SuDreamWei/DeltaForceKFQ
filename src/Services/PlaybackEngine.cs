using System.Diagnostics;
using DeltaHarmonica.Interop;
using DeltaHarmonica.Models;

namespace DeltaHarmonica.Services;

public sealed class PlaybackSettings
{
    /// <summary>Seconds to wait after the confirmation before the first note.</summary>
    public int LeadInMs { get; set; } = 3000;

    /// <summary>Random +/- percent applied to how long each key is held.</summary>
    public int DurationJitterPercent { get; set; } = 12;

    /// <summary>Random 0..N ms inserted between the parts of one key combination.</summary>
    public int TimingJitterMs { get; set; } = 8;

    public bool Humanise { get; set; } = true;

    /// <summary>Speed multiplier. 1.0 = as written, 1.2 = 20% faster, 0.8 = slower.</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>Skip the confirmation prompt and the lead-in countdown.</summary>
    public bool SkipLeadIn { get; set; }
}

public enum PlaybackState { Idle, Countdown, Playing, Paused }

/// <summary>
/// Serialises note actions to the game.
///
/// Threading model
/// ---------------
/// All key/mouse emission happens in ONE worker thread at a time. Starting a new
/// run waits for the previous worker to fully exit before emitting anything, and
/// every emission is guarded by <see cref="_emitLock"/>. Without that, a restart
/// could have the old thread releasing keys while the new one pressed them, which
/// shows up in-game as random modifier+key combinations ("乱按按键").
///
/// Every sleep inside a note is interruptible, so Stop() takes effect within a few
/// milliseconds instead of finishing the note first. Stop() never blocks the caller
/// for longer than a short grace period, so the UI stays responsive.
///
/// Timing
/// ------
/// Notes are scheduled against an absolute clock and the time each keystroke
/// actually consumed is subtracted, so error cannot accumulate over a long song.
/// </summary>
public sealed class PlaybackEngine
{
    private static readonly int[] VkForScaleKey = { 0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, 0xBC };
    // Z X C V B N M VK_OEM_COMMA

    /// <summary>Guards every key/mouse emission across all threads.</summary>
    private static readonly object EmitLock = new();

    private readonly Random _rng = new();
    private Thread? _thread;

    private volatile bool _stop;
    private volatile bool _pause;
    private readonly object _pauseLock = new();

    public PlaybackSettings Settings { get; } = new();

    public event Action<PlaybackState>? StateChanged;
    public event Action<double, double>? ProgressChanged;   // elapsed, total
    public event Action<int, int>? NotePlayed;               // index, count
    public event Action<string>? StatusChanged;

    public Song? CurrentSong { get; private set; }

    /// <summary>Locked so the UI thread never reads a half-updated song.</summary>
    private readonly object _stateLock = new();

    private PlaybackState _state = PlaybackState.Idle;
    public PlaybackState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
        }
    }

    public bool IsBusy => State is PlaybackState.Playing or PlaybackState.Countdown or PlaybackState.Paused;

    // ------------------------------------------------------------------ control

    /// <summary>
    /// Start playing. Safe to call from the UI thread: the previous run is shut
    /// down before this returns, so nothing can race the new thread.
    /// </summary>
    public bool Play(Song song, bool skipLeadIn = false)
    {
        // 1. Fully retire the previous run. This is the critical step: we must not
        //    spawn the new worker until the old one has stopped emitting.
        ShutdownPrevious();

        lock (_stateLock)
        {
            CurrentSong = song;
        }

        _stop = false;
        _pause = false;

        var actions = song.ActionsInRange();
        if (actions.Count == 0) return false;

        double speed = Settings.Speed <= 0 ? 1.0 : Settings.Speed;
        bool lead = !(skipLeadIn || Settings.SkipLeadIn);

        var worker = new Thread(() => Run(song, actions, lead, speed))
        {
            IsBackground = true,
            Name = "Playback",
        };
        _thread = worker;
        worker.Start();
        return true;
    }

    /// <summary>
    /// Stop emitting and wait (briefly) for the worker to exit. Never blocks the
    /// caller for more than <paramref name="graceMs"/>.
    /// </summary>
    public void Stop(int graceMs = 400)
    {
        _stop = true;
        _pause = false;
        lock (_pauseLock) Monitor.PulseAll(_pauseLock);   // wake a paused worker

        var t = _thread;
        if (t is { IsAlive: true } && t != Thread.CurrentThread)
            t.Join(graceMs);

        _thread = null;

        // If the worker overran the grace period it is still winding down; it will
        // release its own keys. Releasing here as well is harmless (releasing a key
        // that is not held is a no-op) and guarantees a clean state.
        ReleaseEverything();
        State = PlaybackState.Idle;
    }

    private void ShutdownPrevious()
    {
        _stop = true;
        _pause = false;
        lock (_pauseLock) Monitor.PulseAll(_pauseLock);

        var t = _thread;
        if (t is { IsAlive: true } && t != Thread.CurrentThread)
        {
            // Wait for the old worker to finish its current note and exit. It checks
            // _stop between notes and inside every sleep, so this is quick.
            if (!t.Join(1500))
            {
                // It is stuck; make sure nothing is held before a new run starts.
                lock (EmitLock) { ReleaseEverythingLocked(); }
            }
        }
        _thread = null;
        ReleaseEverything();
    }

    public void TogglePause()
    {
        if (State == PlaybackState.Paused) Resume();
        else if (State == PlaybackState.Playing) Pause();
    }

    public void Pause()
    {
        if (State != PlaybackState.Playing) return;
        _pause = true;
        State = PlaybackState.Paused;
        ReleaseEverything();
    }

    public void Resume()
    {
        if (!_pause) return;
        _pause = false;
        lock (_pauseLock) Monitor.PulseAll(_pauseLock);
    }

    // ------------------------------------------------------------------ worker

    private void Run(Song song, List<NoteAction> actions, bool lead, double speed)
    {
        try
        {
            if (lead)
            {
                int leadMs = Math.Max(0, Settings.LeadInMs);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < leadMs)
                {
                    if (_stop) return;
                    int remain = (int)((leadMs - sw.ElapsedMilliseconds + 999) / 1000);
                    StatusChanged?.Invoke($"准备中… {remain} 秒后开始，请切换到游戏窗口");
                    if (InterruptibleSleep(50)) return;
                }
            }

            if (_stop) return;
            State = PlaybackState.Playing;
            StatusChanged?.Invoke("演奏中");

            double total = actions[^1].TimeSec + actions[^1].DurationSec;
            var clock = Stopwatch.StartNew();
            double pausedTotal = 0;
            int i = 0;

            while (i < actions.Count)
            {
                if (_stop) break;

                if (_pause)
                {
                    var psw = Stopwatch.StartNew();
                    lock (_pauseLock)
                    {
                        while (_pause && !_stop) Monitor.Wait(_pauseLock, 100);
                    }
                    psw.Stop();
                    pausedTotal += psw.Elapsed.TotalSeconds;
                    if (_stop) break;
                    State = PlaybackState.Playing;
                }

                var a = actions[i];
                double target = a.TimeSec / speed;

                // ---- wait until this note is due ----
                bool interrupted = false;
                for (;;)
                {
                    if (_stop) { interrupted = true; break; }
                    if (_pause) { interrupted = true; break; }
                    double elapsed = clock.Elapsed.TotalSeconds - pausedTotal;
                    double remain = target - elapsed;
                    if (remain <= 0.0008) break;
                    if (InterruptibleSleep((int)Math.Clamp(remain * 1000, 1, 15))) { interrupted = true; break; }
                }
                if (interrupted)
                {
                    if (_stop) break;
                    continue;
                }

                if (!Emit(a, speed)) break;      // returns false if cancelled mid-note

                NotePlayed?.Invoke(i, actions.Count);
                double now = clock.Elapsed.TotalSeconds - pausedTotal;
                ProgressChanged?.Invoke(Math.Min(now, total / speed), total / speed);
                i++;
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("演奏出错：" + ex.Message);
        }
        finally
        {
            // Always hand back a clean keyboard/mouse state.
            ReleaseEverything();
            State = PlaybackState.Idle;
            StatusChanged?.Invoke("已结束");
        }
    }

    /// <summary>
    /// Sleep that wakes early once _stop is set. Returns true if it was interrupted.
    /// Sleeping in small slices keeps Stop() responsive without busy-waiting.
    /// </summary>
    private bool InterruptibleSleep(int ms)
    {
        if (ms <= 0) return _stop;
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (_stop) return true;
            int left = ms - (int)sw.ElapsedMilliseconds;
            if (left <= 0) break;
            Thread.Sleep(Math.Min(left, 5));
        }
        return _stop;
    }

    /// <summary>
    /// Send one note: modifiers down, key tap, modifiers up.
    /// Returns false if playback was cancelled during the note.
    ///
    /// The whole sequence is atomic with respect to other emitters, and every wait
    /// is interruptible, so a stop cannot leave a modifier stuck down or let a new
    /// run interleave with this one.
    /// </summary>
    private bool Emit(NoteAction a, double speed)
    {
        int vk = VkForScaleKey[(int)a.Key];
        int leftDown = a.OctaveShift < 0 ? -a.OctaveShift : 0;
        int rightDown = a.OctaveShift > 0 ? a.OctaveShift : 0;

        lock (EmitLock)
        {
            try
            {
                var sw = Stopwatch.StartNew();

                // Modifiers press in a fixed order; released in reverse below.
                for (int i = 0; i < leftDown; i++) InputSimulator.MouseDown(InputSimulator.MouseButton.Left);
                for (int i = 0; i < rightDown; i++) InputSimulator.MouseDown(InputSimulator.MouseButton.Right);
                if (a.Sharp) InputSimulator.MouseDown(InputSimulator.MouseButton.Middle);

                if (Settings.Humanise && InterruptibleSleep(_rng.Next(0, Settings.TimingJitterMs + 1)))
                    return false;

                InputSimulator.KeyDown(vk);

                double hold = a.DurationSec / speed;
                if (Settings.Humanise) hold = Jitter(hold);
                hold -= sw.Elapsed.TotalSeconds;         // account for modifier presses
                if (hold < 0.012) hold = 0.012;

                bool cancelled = InterruptibleSleep((int)Math.Round(hold * 1000));

                InputSimulator.KeyUp(vk);

                if (cancelled)
                {
                    // Release modifiers immediately; nothing else may be pressed.
                    if (a.Sharp) InputSimulator.MouseUp(InputSimulator.MouseButton.Middle);
                    for (int i = 0; i < rightDown; i++) InputSimulator.MouseUp(InputSimulator.MouseButton.Right);
                    for (int i = 0; i < leftDown; i++) InputSimulator.MouseUp(InputSimulator.MouseButton.Left);
                    return false;
                }

                if (Settings.Humanise) InterruptibleSleep(_rng.Next(0, Settings.TimingJitterMs + 1));

                if (a.Sharp) InputSimulator.MouseUp(InputSimulator.MouseButton.Middle);
                for (int i = 0; i < rightDown; i++) InputSimulator.MouseUp(InputSimulator.MouseButton.Right);
                for (int i = 0; i < leftDown; i++) InputSimulator.MouseUp(InputSimulator.MouseButton.Left);
                return true;
            }
            finally
            {
            }
        }
    }

    private double Jitter(double sec)
    {
        if (Settings.DurationJitterPercent <= 0) return sec;
        double pct = (_rng.NextDouble() * 2 - 1) * Settings.DurationJitterPercent / 100.0;
        return sec * (1 + pct);
    }

    /// <summary>Release every key and mouse button we might be holding.</summary>
    public static void ReleaseEverything()
    {
        lock (EmitLock) { ReleaseEverythingLocked(); }
    }

    private static void ReleaseEverythingLocked()
    {
        foreach (var vk in VkForScaleKey) InputSimulator.KeyUp(vk);
        InputSimulator.MouseUp(InputSimulator.MouseButton.Left);
        InputSimulator.MouseUp(InputSimulator.MouseButton.Middle);
        InputSimulator.MouseUp(InputSimulator.MouseButton.Right);
    }
}
