using System.Runtime.InteropServices;
using DeltaHarmonica.Models;

namespace DeltaHarmonica.Services;

/// <summary>
/// Plays a song audibly through the PC speakers so you can audition it before
/// sending anything to the game. A small additive synth (fundamental + a couple
/// of harmonics with a soft envelope) that sounds flute-ish, driven by waveOut.
///
/// This is purely a listening aid - it never touches the game.
/// </summary>
public sealed class AuditionPlayer : IDisposable
{
    private const int SampleRate = 44100;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int BufferCount = 4;
    private const int FramesPerBuffer = 2048;

    private readonly object _lock = new();
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _pause;
    private readonly object _pauseLock = new();

    private double _masterVolume = 0.35;
    private int _transpose = 0;

    public event Action<double, double>? ProgressChanged;   // elapsed, total
    public event Action? Finished;

    public bool IsPlaying => _thread is { IsAlive: true } && !_stop;
    public bool IsPaused => _pause;

    public double MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0, 1);
    }

    /// <summary>Semitone offset applied when auditioning (audible only, not to the game).</summary>
    public int Transpose
    {
        get => _transpose;
        set => _transpose = Math.Clamp(value, -24, 24);
    }

    // ------------------------------------------------------------------ winmm

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private const uint WAVE_MAPPER = 0xFFFFFFFF;
    private const uint WHDR_DONE = 0x00000001;

    [DllImport("winmm.dll")] private static extern int waveOutOpen(out IntPtr hWaveOut, uint uDeviceID,
        ref WAVEFORMATEX lpFormat, IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);
    [DllImport("winmm.dll")] private static extern int waveOutClose(IntPtr hWaveOut);
    [DllImport("winmm.dll")] private static extern int waveOutPrepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport("winmm.dll")] private static extern int waveOutUnprepareHeader(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport("winmm.dll")] private static extern int waveOutWrite(IntPtr hWaveOut, IntPtr lpWaveOutHdr, int uSize);
    [DllImport("winmm.dll")] private static extern int waveOutReset(IntPtr hWaveOut);
    [DllImport("winmm.dll")] private static extern int waveOutSetVolume(IntPtr hWaveOut, uint dwVolume);

    // ------------------------------------------------------------------ control

    public void Play(Song song, double speed = 1.0)
    {
        Stop();
        if (song.Actions.Count == 0) return;

        // Snapshot so edits during playback cannot corrupt the render.
        var notes = song.ActionsInRange().Select(a => a.Clone()).ToList();
        int basePitch = song.BaseMidiPitch;
        double total = notes.Count > 0
            ? notes.Max(n => n.TimeSec + n.DurationSec)
            : 0;
        double sp = speed <= 0 ? 1.0 : speed;

        _stop = false;
        _pause = false;
        _thread = new Thread(() => RenderLoop(notes, basePitch, total, sp))
        {
            IsBackground = true,
            Name = "Audition",
        };
        _thread.Start();
    }

    public void Stop(int graceMs = 300)
    {
        _stop = true;
        Resume();
        var t = _thread;
        if (t is { IsAlive: true } && t != Thread.CurrentThread)
            t.Join(graceMs);                 // bounded: never freeze the UI thread
        _thread = null;
    }

    public void TogglePause()
    {
        if (_pause) Resume();
        else Pause();
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        _pause = true;
    }

    public void Resume()
    {
        if (!_pause) return;
        _pause = false;
        lock (_pauseLock) Monitor.PulseAll(_pauseLock);
    }

    // ------------------------------------------------------------------ render

    private void RenderLoop(List<NoteAction> notes, int basePitch, double total, double speed)
    {
        IntPtr hOut = IntPtr.Zero;
        var format = new WAVEFORMATEX
        {
            wFormatTag = 1,                     // PCM
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (ushort)(Channels * BitsPerSample / 8),
            nAvgBytesPerSec = (uint)(SampleRate * Channels * BitsPerSample / 8),
            cbSize = 0,
        };

        if (waveOutOpen(out hOut, WAVE_MAPPER, ref format, IntPtr.Zero, IntPtr.Zero, 0) != 0)
        {
            Finished?.Invoke();
            return;
        }

        int bufBytes = FramesPerBuffer * Channels * (BitsPerSample / 8);
        var headers = new IntPtr[BufferCount];
        var dataPtrs = new IntPtr[BufferCount];
        var queued = new bool[BufferCount];

        try
        {
            for (int i = 0; i < BufferCount; i++)
            {
                dataPtrs[i] = Marshal.AllocHGlobal(bufBytes);
                var hdr = new WAVEHDR
                {
                    lpData = dataPtrs[i],
                    dwBufferLength = (uint)bufBytes,
                    dwFlags = 0,
                };
                headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEHDR>());
                Marshal.StructureToPtr(hdr, headers[i], false);
                waveOutPrepareHeader(hOut, headers[i], Marshal.SizeOf<WAVEHDR>());
            }

            double samplePos = 0;                 // output frames rendered
            double elapsed = 0;
            double pausedTotal = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int slot = 0;
            var buf = new short[FramesPerBuffer];

            // Stop once the whole song has been rendered, plus room for the last
            // note's release tail.
            double endSample = (total / Math.Max(0.01, speed) + 0.35) * SampleRate;

            while (!_stop && samplePos < endSample)
            {
                // ---- pause ----
                if (_pause)
                {
                    var pw = System.Diagnostics.Stopwatch.StartNew();
                    lock (_pauseLock) { while (_pause && !_stop) Monitor.Wait(_pauseLock, 100); }
                    pw.Stop();
                    pausedTotal += pw.Elapsed.TotalSeconds;
                    if (_stop) break;
                }

                // Wait for this slot to be free. A freshly prepared header is NOT
                // done yet, so "never queued" has to be tracked separately -
                // checking dwBufferLength/dwFlags alone would block on the first
                // buffer forever.
                while (!_stop)
                {
                    if (!queued[slot]) break;
                    var h = Marshal.PtrToStructure<WAVEHDR>(headers[slot]);
                    if ((h.dwFlags & WHDR_DONE) != 0) break;
                    Thread.Sleep(3);
                }
                if (_stop) break;

                // ---- synthesise one buffer ----
                // Output time is wall-clock; the score advances `speed` times faster,
                // so convert before sampling the notes.
                double t0 = samplePos / SampleRate * speed;
                double t1 = (samplePos + FramesPerBuffer) / SampleRate * speed;
                RenderBlock(buf, notes, basePitch, t0, t1, speed);

                Marshal.Copy(buf, 0, dataPtrs[slot], FramesPerBuffer);

                var wh = Marshal.PtrToStructure<WAVEHDR>(headers[slot]);
                wh.dwFlags &= ~WHDR_DONE;
                wh.dwBufferLength = (uint)bufBytes;
                Marshal.StructureToPtr(wh, headers[slot], false);

                waveOutWrite(hOut, headers[slot], Marshal.SizeOf<WAVEHDR>());
                queued[slot] = true;

                slot = (slot + 1) % BufferCount;
                samplePos += FramesPerBuffer;

                elapsed = clock.Elapsed.TotalSeconds - pausedTotal;
                if (total > 0) ProgressChanged?.Invoke(Math.Min(elapsed, total / speed), total / speed);
            }

            waveOutReset(hOut);
            for (int i = 0; i < BufferCount; i++)
            {
                waveOutUnprepareHeader(hOut, headers[i], Marshal.SizeOf<WAVEHDR>());
                Marshal.FreeHGlobal(headers[i]);
                Marshal.FreeHGlobal(dataPtrs[i]);
            }
        }
        finally
        {
            if (hOut != IntPtr.Zero) waveOutClose(hOut);
            Finished?.Invoke();
        }
    }

    /// <summary>
    /// Additive synth for the notes overlapping [t0, t1) in score time.
    ///
    /// Everything here is computed from ABSOLUTE output time so the waveform is
    /// continuous across buffer boundaries. Deriving the phase from a per-buffer
    /// local index would reset it to zero every buffer, which forces the amplitude
    /// to zero at each seam and makes the result sound like a series of clicks
    /// ("dot matrix") rather than a continuous melody.
    ///
    /// The envelope is deliberately legato: notes that run into each other are
    /// joined with only a tiny dip, so a scale sounds connected the way a wind
    /// instrument does, instead of staccato blips.
    /// </summary>
    private void RenderBlock(short[] buf, List<NoteAction> notes, int basePitch,
                             double t0, double t1, double speed)
    {
        Array.Clear(buf, 0, buf.Length);
        if (speed <= 0) speed = 1.0;

        // Buffer window in OUTPUT time.
        double outT0 = t0 / speed;
        double outT1 = t1 / speed;

        var acc = new double[buf.Length];

        foreach (var n in notes)
        {
            double scoreStart = n.TimeSec;
            double scoreEnd = n.TimeSec + n.DurationSec;
            if (scoreEnd <= t0) continue;
            if (scoreStart >= t1) break;                 // notes are time-ordered

            int pitch = n.PitchWithBase(basePitch) + _transpose;
            if (pitch is < 0 or > 127) continue;
            double freq = 440.0 * Math.Pow(2, (pitch - 69) / 12.0);

            // Note extent in output time, in absolute seconds.
            double noteStart = scoreStart / speed;
            double noteEnd = scoreEnd / speed;
            double noteDur = Math.Max(0.02, noteEnd - noteStart);

            // How much this note may bleed past its written end (a short release
            // tail). It IS allowed to overlap the next note, which is what keeps
            // consecutive notes connected instead of separated by silence.
            double tail = Math.Min(0.09, Math.Max(0.03, noteDur * 0.35));

            int i0 = (int)Math.Clamp(Math.Round((noteStart - outT0) * SampleRate), 0, buf.Length);
            int i1 = (int)Math.Clamp(Math.Round((noteEnd + tail - outT0) * SampleRate), 0, buf.Length);

            for (int i = i0; i < i1; i++)
            {
                // Absolute output time for this sample - keeps the phase continuous.
                double t = outT0 + i / (double)SampleRate;
                if (t < noteStart) continue;

                double since = t - noteStart;            // seconds since note began
                double until = noteEnd - t;              // seconds left in the written note

                // ---- envelope (legato) ----
                // Soft, longer attack avoids a click at note onset.
                const double attack = 0.018;
                double env = since < attack ? since / attack : 1.0;

                if (until <= 0)
                {
                    // Release tail: fade out smoothly, never a hard cut.
                    double into = -until;
                    env *= Math.Max(0, 1.0 - into / tail);
                    env *= env;                          // smoother than linear
                }
                else if (until < attack)
                {
                    // Handing over to the next note: dip only slightly so the line
                    // stays connected.
                    env *= 0.72 + 0.28 * (until / attack);
                }
                else
                {
                    // Very gentle body decay, and never lower than ~0.8, so a held
                    // note keeps its level for its whole length.
                    env *= 0.80 + 0.20 * Math.Exp(-1.1 * since / noteDur);
                }

                if (env <= 0) continue;

                // Phase from ABSOLUTE time => no discontinuity at buffer seams.
                double phase = 2 * Math.PI * freq * t;
                double s = Math.Sin(phase)
                         + 0.30 * Math.Sin(2 * phase)
                         + 0.12 * Math.Sin(3 * phase)
                         + 0.04 * Math.Sin(4 * phase);
                acc[i] += s * env * 0.20;
            }
        }

        for (int i = 0; i < buf.Length; i++)
        {
            double v = acc[i] * _masterVolume;
            v = Math.Tanh(v);                            // soft clip, no hard edges
            buf[i] = (short)(v * 32000);
        }
    }

    public void Dispose() => Stop();
}
