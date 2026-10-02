// Renders the audition synth offline and analyses it for the "dot matrix"
// problem: gaps or dips between consecutive notes.
using DeltaHarmonica.Models;
using DeltaHarmonica.Services;
using System.Reflection;

Console.OutputEncoding = System.Text.Encoding.UTF8;
int fail = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) fail++;
}

const int SR = 44100;
const int Block = 2048;
const int SilenceThreshold = 60;   // out of 32767

var player = new AuditionPlayer { MasterVolume = 0.8 };
var render = typeof(AuditionPlayer)
    .GetMethod("RenderBlock", BindingFlags.NonPublic | BindingFlags.Instance)
    ?? throw new InvalidOperationException("RenderBlock not found");

// A rising scale with notes joined end to end - the pattern that sounded like dots.
var song = new Song { Title = "scale", LengthSec = 8, BaseMidiPitch = 60 };
double t = 0;
foreach (var key in new[] { ScaleKey.Z, ScaleKey.X, ScaleKey.C, ScaleKey.V,
                            ScaleKey.B, ScaleKey.N, ScaleKey.M })
{
    song.Actions.Add(new NoteAction { TimeSec = t, DurationSec = 0.5, Key = key });
    t += 0.5;
}
double total = song.Actions.Max(a => a.TimeSec + a.DurationSec);

(short[] Data, int Valid) RenderWhole(double sp)
{
    int frames = (int)Math.Ceiling((total / sp + 0.5) * SR / Block) * Block;
    var dest = new short[frames];
    int valid = 0;
    for (int pos = 0; pos + Block <= frames; pos += Block)
    {
        var buf = new short[Block];
        double t0 = pos / (double)SR * sp;
        double t1 = (pos + Block) / (double)SR * sp;
        render.Invoke(player, new object[] { buf, song.Actions, song.BaseMidiPitch, t0, t1, sp });
        Array.Copy(buf, 0, dest, pos, Block);
        valid = pos + Block;
    }
    return (dest, valid);
}

// ---- 1. continuity at 1x ----
Console.WriteLine("=== 1. Continuity at 1x speed ===");
var (full, validFrames) = RenderWhole(1.0);

// Only the musical region is analysed. Sound legitimately stops after the last
// note (its release tail fades out), and the render allocation is rounded up to a
// whole number of buffers, so both regions after `total` are expected silence and
// must not be counted as gaps.
int musicFrames = (int)Math.Min(validFrames, total * SR);

int longestSilent = 0, curRun = 0, firstSound = -1, lastSound = -1;
for (int i = 0; i < musicFrames; i++)
{
    if (Math.Abs(full[i]) < SilenceThreshold)
    {
        curRun++;
        if (curRun > longestSilent) longestSilent = curRun;
    }
    else
    {
        if (firstSound < 0) firstSound = i;
        lastSound = i;
        curRun = 0;
    }
}

double longestSilentMs = longestSilent / (double)SR * 1000;
Console.WriteLine($"  analysed 0..{musicFrames / (double)SR:F2}s (notes end at {total:F2}s)");
Console.WriteLine($"  first sound @ {firstSound / (double)SR:F3}s, last @ {lastSound / (double)SR:F3}s");
Console.WriteLine($"  longest silent run inside the music: {longestSilentMs:F1} ms");

Check(longestSilentMs < 12,
      $"no silent gap longer than 12ms anywhere (got {longestSilentMs:F1} ms)");
Check(lastSound / (double)SR > total - 0.15,
      $"audio continues to the end of the last note ({lastSound / (double)SR:F2}s)");

int silentWindows = 0;
for (double s = 0; s + 0.25 <= total; s += 0.25)
{
    int a = (int)(s * SR), b = (int)((s + 0.25) * SR);
    double r = 0;
    for (int i = a; i < b && i < musicFrames; i++) r += (double)full[i] * full[i];
    r = Math.Sqrt(r / Math.Max(1, b - a));
    if (r < SilenceThreshold) silentWindows++;
}
Console.WriteLine($"  silent 250ms windows across the song: {silentWindows}");
Check(silentWindows == 0, "no 250ms window anywhere is silent");

// ---- 2. buffer seams must not dip ----
Console.WriteLine("\n=== 2. Buffer seams ===");
int seamDips = 0;
for (int pos = Block; pos + Block < validFrames; pos += Block)
{
    double seamAmp = 0, aroundAmp = 0;
    for (int k = -40; k < 40; k++)
        if (pos + k >= 0 && pos + k < validFrames)
            seamAmp = Math.Max(seamAmp, Math.Abs(full[pos + k]));
    for (int k = 200; k < 240; k++)
        if (pos + k < validFrames) aroundAmp = Math.Max(aroundAmp, Math.Abs(full[pos + k]));

    if (aroundAmp > 200 && seamAmp < aroundAmp * 0.25) seamDips++;
}
Console.WriteLine($"  buffer seams with a severe amplitude dip: {seamDips}");
Check(seamDips == 0, "no buffer seam collapses the waveform (phase is continuous)");

// ---- 3. melody content ----
Console.WriteLine("\n=== 3. Melody ===");
var est = new List<double>();
for (int n = 0; n < song.Actions.Count; n++)
{
    int a = (int)((song.Actions[n].TimeSec + 0.12) * SR);
    int b = (int)((song.Actions[n].TimeSec + 0.40) * SR);
    if (b >= validFrames) break;
    int crossings = 0;
    for (int i = a + 1; i < b; i++)
        if (full[i - 1] < 0 && full[i] >= 0) crossings++;
    est.Add(crossings / ((b - a) / (double)SR));
}
Console.WriteLine("  estimated: " + string.Join(", ", est.Select(h => $"{h:F0}Hz")));
Console.WriteLine("  expected:  " + string.Join(", ", song.Actions.Select(a =>
    $"{440.0 * Math.Pow(2, (a.PitchWithBase(60) - 69) / 12.0):F0}Hz")));

Check(est.Count == 7, "all 7 notes rendered");
bool rising = true;
for (int i = 1; i < est.Count; i++) if (est[i] <= est[i - 1]) rising = false;
Check(rising, "estimated pitch rises across the scale");

bool pitchClose = true;
for (int i = 0; i < est.Count && i < song.Actions.Count; i++)
{
    double want = 440.0 * Math.Pow(2, (song.Actions[i].PitchWithBase(60) - 69) / 12.0);
    if (Math.Abs(est[i] - want) / want > 0.15) pitchClose = false;
}
Check(pitchClose, "estimated pitches match the intended notes");

// ---- 4. level ----
Console.WriteLine("\n=== 4. Level ===");
double rms = 0;
int counted = 0;
for (int i = firstSound; i <= lastSound && i < validFrames; i++) { rms += (double)full[i] * full[i]; counted++; }
rms = Math.Sqrt(rms / Math.Max(1, counted));
Console.WriteLine($"  overall RMS = {rms:F0} (full scale 32767)");
Check(rms > 800, "overall level is healthy, not fading out");

// ---- 5. continuity at other speeds ----
Console.WriteLine("\n=== 5. Continuity at other speeds ===");
foreach (var sp in new[] { 0.5, 1.5, 2.0 })
{
    var (data, valid) = RenderWhole(sp);
    double scaledTotal = total / sp;
    int frames = (int)Math.Min(valid, scaledTotal * SR);
    int longest = 0; curRun = 0;
    for (int i = 0; i < frames; i++)
    {
        if (Math.Abs(data[i]) < SilenceThreshold) { curRun++; longest = Math.Max(longest, curRun); }
        else curRun = 0;
    }
    double ms = longest / (double)SR * 1000;
    Console.WriteLine($"  {sp:0.0}x -> longest silent run {ms:F1} ms (over {scaledTotal:F2}s of music)");
    Check(ms < 12, $"{sp:0.0}x playback is continuous");
}

Console.WriteLine("\n==============================");
Console.WriteLine(fail == 0 ? "ALL CONTINUITY CHECKS PASSED" : $"{fail} CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
