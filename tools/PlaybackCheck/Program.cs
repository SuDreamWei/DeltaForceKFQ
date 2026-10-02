// Concurrency and timing tests for PlaybackEngine.
//
// The dangerous bug this guards against: restarting playback while a note was in
// flight used to let two worker threads emit key events at the same time, which
// the game sees as random modifier+key combinations ("乱按按键").
//
// Note: this emits REAL keystrokes. Run it with a harmless window focused.
using System.Diagnostics;
using System.Reflection;
using DeltaHarmonica.Models;
using DeltaHarmonica.Services;

Console.OutputEncoding = System.Text.Encoding.UTF8;
int fail = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) fail++;
}

static Song MakeSong(double noteDur, int count, double gap)
{
    var s = new Song { Title = "t", LengthSec = count * gap + noteDur, BaseMidiPitch = 60 };
    for (int i = 0; i < count; i++)
    {
        s.Actions.Add(new NoteAction
        {
            TimeSec = i * gap,
            DurationSec = noteDur,
            Key = (ScaleKey)(i % 8),
            OctaveShift = i % 3 - 1,
            Sharp = i % 2 == 0,
        });
    }
    return s;
}

// ---------------------------------------------------------------- 1. stop latency
Console.WriteLine("=== 1. Stop() responsiveness ===");
{
    var e = new PlaybackEngine();
    e.Settings.SkipLeadIn = true;
    e.Settings.Humanise = false;

    e.Play(MakeSong(noteDur: 3.0, count: 5, gap: 3.0), skipLeadIn: true);
    Thread.Sleep(250);          // land in the middle of a 3s note

    var sw = Stopwatch.StartNew();
    e.Stop();
    sw.Stop();

    Console.WriteLine($"  Stop() during a 3s note took {sw.ElapsedMilliseconds} ms");
    Check(sw.ElapsedMilliseconds < 700,
          $"Stop() returns promptly instead of waiting out the note ({sw.ElapsedMilliseconds} ms)");
    Check(!e.IsBusy, "engine is idle after Stop()");
}

// ------------------------------------------------------- 2. rapid restart churn
Console.WriteLine("\n=== 2. Rapid restart (the '乱按按键' case) ===");
{
    var e = new PlaybackEngine();
    e.Settings.SkipLeadIn = true;
    e.Settings.Humanise = false;

    var songA = MakeSong(noteDur: 0.35, count: 40, gap: 0.4);
    var songB = MakeSong(noteDur: 0.20, count: 60, gap: 0.25);

    var sw = Stopwatch.StartNew();
    for (int i = 0; i < 25; i++)
    {
        e.Play(i % 2 == 0 ? songA : songB, skipLeadIn: true);
        Thread.Sleep(35);       // interrupt mid-note, over and over
    }
    sw.Stop();
    Console.WriteLine($"  25 restarts in {sw.ElapsedMilliseconds} ms");
    Check(sw.ElapsedMilliseconds < 15000, "rapid restarts complete without deadlock");

    e.Stop();
    Thread.Sleep(150);

    var field = typeof(PlaybackEngine).GetField("_thread",
        BindingFlags.NonPublic | BindingFlags.Instance);
    var thread = field?.GetValue(e) as Thread;
    Check(thread is null || !thread.IsAlive, "no playback thread survives the churn");
    Check(!e.IsBusy, "engine settled to idle");
}

// --------------------------------------------------- 3. state consistency
Console.WriteLine("\n=== 3. State and events ===");
{
    var e = new PlaybackEngine();
    e.Settings.SkipLeadIn = true;
    e.Settings.Humanise = false;

    int stateChanges = 0;
    var states = new List<PlaybackState>();
    e.StateChanged += s => { Interlocked.Increment(ref stateChanges); lock (states) states.Add(s); };

    int played = 0;
    e.NotePlayed += (_, __) => Interlocked.Increment(ref played);

    bool finished = false;
    e.StatusChanged += s => { if (s == "已结束") finished = true; };

    var song = MakeSong(noteDur: 0.05, count: 8, gap: 0.08);
    e.Play(song, skipLeadIn: true);

    var deadline = DateTime.UtcNow.AddSeconds(10);
    while (!finished && DateTime.UtcNow < deadline) Thread.Sleep(50);

    Console.WriteLine($"  notes emitted: {played}/{song.Actions.Count}, state changes: {stateChanges}");
    Check(finished, "playback reported completion");
    Check(played == song.Actions.Count, "every note was emitted exactly once");
    Check(!e.IsBusy, "engine idle after natural completion");

    lock (states)
    {
        Check(states.Contains(PlaybackState.Playing), "engine passed through Playing");
        Check(states[^1] == PlaybackState.Idle, "engine ended in Idle");
    }
}

// --------------------------------------------------- 4. cancel mid-run
Console.WriteLine("\n=== 4. Cancel mid-run ===");
{
    var e = new PlaybackEngine();
    e.Settings.SkipLeadIn = true;
    e.Settings.Humanise = false;

    int played = 0;
    e.NotePlayed += (_, __) => Interlocked.Increment(ref played);

    var song = MakeSong(noteDur: 0.05, count: 200, gap: 0.06);
    e.Play(song, skipLeadIn: true);
    Thread.Sleep(400);
    int atStop = played;
    e.Stop();
    Thread.Sleep(250);

    Console.WriteLine($"  stopped after {atStop} notes; count settled at {played}");
    Check(atStop > 0, "some notes played before the stop");
    Check(played < song.Actions.Count, "stopping prevented the rest of the song");
    Check(!e.IsBusy, "engine idle after mid-run stop");
}

// --------------------------------------------------- 5. replay after stop
Console.WriteLine("\n=== 5. Play again after stop ===");
{
    var e = new PlaybackEngine();
    e.Settings.SkipLeadIn = true;
    e.Settings.Humanise = false;

    var song = MakeSong(noteDur: 0.04, count: 6, gap: 0.07);

    for (int round = 0; round < 3; round++)
    {
        bool done = false;
        void OnStatus(string s) { if (s == "已结束") done = true; }
        e.StatusChanged += OnStatus;

        e.Play(song, skipLeadIn: true);
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!done && DateTime.UtcNow < deadline) Thread.Sleep(40);
        e.StatusChanged -= OnStatus;

        Check(done, $"round {round + 1} completed");
    }
}

// --------------------------------------------------- 6. speed (倍速)
Console.WriteLine("\n=== 6. Speed (倍速) precision ===");
{
    foreach (var sp in new[] { 0.5, 0.8, 1.0, 1.2, 1.5, 2.0 })
    {
        var e = new PlaybackEngine();
        e.Settings.SkipLeadIn = true;
        e.Settings.Humanise = false;
        e.Settings.Speed = sp;

        var song = new Song { LengthSec = 10, BaseMidiPitch = 60 };
        for (int i = 0; i < 6; i++)
            song.Actions.Add(new NoteAction { TimeSec = i * 0.2, DurationSec = 0.05, Key = ScaleKey.Z });

        var times = new List<double>();
        var sw = Stopwatch.StartNew();
        e.NotePlayed += (_, __) => { lock (times) times.Add(sw.Elapsed.TotalSeconds); };

        bool done = false;
        e.StatusChanged += s => { if (s == "已结束") done = true; };

        e.Play(song, true);
        var dl = DateTime.UtcNow.AddSeconds(20);
        while (!done && DateTime.UtcNow < dl) Thread.Sleep(10);
        e.Stop();

        double span = times.Count >= 2 ? times[^1] - times[0] : 0;
        double expected = 5 * 0.2 / sp;      // five 0.2s gaps, compressed by speed

        Console.WriteLine($"  {sp:0.0}x -> span {span:F3}s, expected {expected:F3}s " +
                          $"(ratio {(expected > 0 ? span / expected : 0):F3})");

        // The only slack is the fixed per-note cost, so allow 8%.
        Check(span > 0 && Math.Abs(span - expected) / expected < 0.08,
              $"{sp:0.0}x scales the note spacing correctly");
    }
}

Console.WriteLine("\n==============================");
Console.WriteLine(fail == 0 ? "ALL PLAYBACK CHECKS PASSED" : $"{fail} CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
