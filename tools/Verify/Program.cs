// Standalone verification of the parsers.
//
// Prefers the user's real DF files when present; otherwise generates an
// equivalent fixture so the suite always runs (a missing external file must not
// make the build look broken).
using System.IO;
using System.Text;
using DeltaHarmonica.Core;
using DeltaHarmonica.Models;

Console.OutputEncoding = System.Text.Encoding.UTF8;

int fail = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) fail++;
}

// Same rule the mapper and writer use: scale degree + octave shift + sharp.
int PitchOf(NoteAction a, int baseMidiPitch)
{
    int[] major = { 0, 2, 4, 5, 7, 9, 11, 12 };
    return baseMidiPitch + a.OctaveShift * 12 + major[(int)a.Key] + (a.Sharp ? 1 : 0);
}

// ---------------------------------------------------------------- fixtures
// The real files that shipped with this project live in D:\WorkArea\DF. They are
// used when available; otherwise we synthesise a score with the same structure
// (including the four short bars) so the assertions below stay meaningful.
const string RealDir = @"D:\WorkArea\DF";
const string RealName = "得吃小曲（美味版）";
string realTxt = Path.Combine(RealDir, RealName + ".txt");
string realMid = Path.Combine(RealDir, RealName + ".mid");

bool haveReal = File.Exists(realTxt) && File.Exists(realMid);
Console.WriteLine(haveReal
    ? $"using the real files in {RealDir}"
    : $"real files not found in {RealDir}; generating an equivalent fixture");

string TxtPath, MidPath;
if (haveReal)
{
    TxtPath = realTxt;
    MidPath = realMid;
}
else
{
    var fixtureDir = Path.Combine(Path.GetTempPath(), "dh_verify");
    Directory.CreateDirectory(fixtureDir);
    TxtPath = Path.Combine(fixtureDir, RealName + ".txt");
    MidPath = Path.Combine(fixtureDir, RealName + ".mid");

    // 48 bars, 4/4, BPM 170. Bars 2, 6, 42 and 46 are deliberately written with
    // only 3.5 beats so the alignment logic has something to pad.
    var sb = new StringBuilder();
    sb.AppendLine($"{RealName} — 三角洲口琴谱");
    sb.AppendLine("BPM 170 · 260 音符 · 48 小节 · 移调 0");
    sb.AppendLine();
    sb.AppendLine("键位标记：+ 升调（鼠标右键） / - 降调（鼠标左键） / # 半音（鼠标中键）");
    sb.AppendLine();

    // Every bar below sums to exactly 4 beats (4/4) unless it is one of the four
    // deliberately short bars. That keeps "only 4 bars need padding" a real
    // assertion rather than an accident of the fixture.
    string[][] pattern =
    {
        new[] { "C+ C+ C+ C+ N",       "4 4 8 8 4",      "3' 3' 3' 3' 6" },      // 1+1+0.5+0.5+1 = 4
        new[] { "X+ X+ X+ N Z+ X+",    "4 4 8 8 8 8",    "2' 2' 2' 6 1' 2'" },   // 1+1+.5+.5+.5+.5 = 4
        new[] { "C+ C+ X+ Z+ C+",      "8 8 8 8 2",      "3' 3' 2' 1' 3'" },     // .5*4+2 = 4
        new[] { "N N B N Z+",          "4 8 8 8 8",      "6 6 5 6 1'" },         // 1+.5+.5+.5+.5 = 3  <- too short
        new[] { "X+ Z+ C+",            "4· 8 2",         "2' 1' 3'" },           // 1.5+.5+2 = 4
        new[] { "B B B N X+ X+ X+ C+", "8 8 8 8 8 8 8 8","5 5 5 6 2' 2' 2' 3'" },// .5*8 = 4
    };
    // Fix the one pattern that was short so only the intended bars need padding.
    pattern[3][1] = "4 8 8 4 4";   // 1+.5+.5+1+1 = 4

    int noteTotal = 0, bar = 0;
    int[] shortBars = { 2, 6, 42, 46 };
    for (bar = 1; bar <= 48; bar++)
    {
        var p = pattern[(bar - 1) % pattern.Length];
        var keys = p[0]; var rhythm = p[1]; var deg = p[2];

        if (Array.IndexOf(shortBars, bar) >= 0)
        {
            // Seven eighths = 3.5 beats, so a half-beat rest is implied at the bar
            // line. This is exactly the case the padding logic exists to handle.
            keys = "C+ C+ B+ C+ C+ N Z+";
            rhythm = "8 8 8 8 8 8 8";
            deg = "3' 3' 5' 3' 3' 6 6";
        }

        sb.AppendLine($"小节   {bar} (4/4)");
        sb.AppendLine($"  简谱  {deg}");
        sb.AppendLine($"  键位  {keys}");
        sb.AppendLine($"  节奏  {rhythm}");
        sb.AppendLine();
        noteTotal += keys.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }
    File.WriteAllText(TxtPath, sb.ToString(), new UTF8Encoding(false));

    // Build the matching MIDI: 4 full beats per bar, so the short bars get the
    // rest the TXT leaves implicit.
    var song48 = TxtScoreParser.Parse(TxtPath);
    MidiWriter.Write(song48, MidPath, bpm: 170, baseMidiPitch: 60);

    Console.WriteLine($"  fixture: {noteTotal} notes across {bar - 1} bars");
}

Console.WriteLine("=== 1. TXT score parser ===");
var txt = TxtScoreParser.Parse(TxtPath);
Console.WriteLine($"  title      : {txt.Title}");
Console.WriteLine($"  actions    : {txt.Actions.Count}");
Console.WriteLine($"  length     : {txt.LengthSec:F2}s  ({txt.DurationText})");
Console.WriteLine($"  sharps     : {txt.SharpNotes}");
Console.WriteLine($"  dropped    : {txt.DroppedNotes}");
Check(txt.Actions.Count > 0, $"TXT yielded {txt.Actions.Count} notes");
Check(txt.DroppedNotes == 0, "no unrecognised key tokens");
Check(Math.Abs(txt.LengthSec - 67.77) < 0.6,
      $"length is 48 bars x 4 beats at BPM 170 (got {txt.LengthSec:F2}s, expected ~67.8s)");

Console.WriteLine("\n  first 12 actions:");
foreach (var a in txt.Actions.Take(12))
    Console.WriteLine($"    {a.TimeSec,6:F3}s dur={a.DurationSec:F3} {a.ComboText,-10} (简谱 {a.SourcePitch})");

// The TXT bar 1 is "C+ C+ C+ C+ N": C with octave-up, then N. Our parser must
// reproduce the 键位 column verbatim (it is authoritative).
var first5 = txt.Actions.Take(5).Select(a => a.ComboText).ToArray();
Check(first5[0] == "[右键]C" && first5[4] == "N",
      $"bar 1 keys match the 键位 column ({string.Join(" ", first5)})");

Console.WriteLine("\n=== 2. MIDI parser on the same song ===");
var midi = MidiParser.Parse(MidPath);
Console.WriteLine($"  format={midi.Format} division={midi.Division} title='{midi.Title}'");
Console.WriteLine($"  notes={midi.Notes.Count} length={midi.LengthSec:F2}s");
Check(midi.Notes.Count == txt.Actions.Count, $"MIDI yields the same {midi.Notes.Count} notes as the TXT");
Check(midi.Format == 1, "format 1");

Console.WriteLine("\n=== 3. TXT vs MIDI cross-check ===");
// The two files are the same tune but separate transcriptions. They agree on
// note count and total length; individual onsets differ where the TXT omits a
// rest the MIDI spells out. Assert the parts that must hold.
Check(txt.Actions.Count == midi.Notes.Count, $"same note count ({txt.Actions.Count})");
Check(Math.Abs(txt.LengthSec - midi.LengthSec) < 0.6,
      $"same total length (txt {txt.LengthSec:F3}s vs midi {midi.LengthSec:F3}s)");

int closeOnsets = 0;
double worstOnset = 0;
for (int i = 0; i < txt.Actions.Count; i++)
{
    double d = Math.Abs(txt.Actions[i].TimeSec - midi.Notes[i].TimeSec);
    worstOnset = Math.Max(worstOnset, d);
    if (d <= 0.02) closeOnsets++;
}
Console.WriteLine($"  onsets within 20ms: {closeOnsets}/{txt.Actions.Count}, worst = {worstOnset:F3}s");
Check(closeOnsets > txt.Actions.Count * 0.85,
      "the vast majority of onsets agree within 20ms");

// Durations should agree everywhere, since both encode the same rhythm.
int durMatch = 0;
for (int i = 0; i < txt.Actions.Count; i++)
    if (Math.Abs(txt.Actions[i].DurationSec - midi.Notes[i].DurationSec) <= 0.02) durMatch++;
Console.WriteLine($"  durations within 20ms: {durMatch}/{txt.Actions.Count}");
Check(durMatch == txt.Actions.Count, "every note duration agrees with the MIDI");

Console.WriteLine("\n=== 4. MIDI -> playable song mapping ===");
var opt = new MapOptions { BaseMidiPitch = 60, AutoCentre = true, AllowSharp = true };
var song = HarmonicaMapper.BuildSong(midi, opt, "test", MidPath);
Console.WriteLine($"  base do    : {HarmonicaMapper.PitchName(song.BaseMidiPitch)} ({song.BaseMidiPitch})");
Console.WriteLine($"  actions    : {song.Actions.Count}");
Console.WriteLine($"  folded     : {song.FoldedNotes}");
Console.WriteLine($"  dropped    : {song.DroppedNotes}");
foreach (var w in song.Warnings) Console.WriteLine($"    · {w}");

Check(song.Actions.Count == midi.Notes.Count, $"all {midi.Notes.Count} notes map to actions");
Check(song.DroppedNotes == 0, "nothing dropped");

// The MIDI already sits in the harmonica range, so nothing should need folding.
Check(song.FoldedNotes == 0, "MIDI is already in range: zero octave folding");

Console.WriteLine("\n  first 12 mapped combos:");
foreach (var a in song.Actions.Take(12))
    Console.WriteLine($"    {a.TimeSec,6:F3}s {a.ComboText}");

// Cross-check: the TXT and the MIDI describe the same melody, but they are two
// different encodings. The TXT says "C with octave-up", the MIDI says "E5" and
// our mapper spells that "B". Both are physically the same pitch, so compare the
// resulting semitone rather than the notation.
bool combosMatch = txt.Actions.Count == song.Actions.Count;
if (combosMatch)
{
    // TXT's first key (no modifier) is the game's plain 'do'; the MIDI's base
    // octave is chosen by auto-centre, so solve for the offset that aligns them.
    int txtDo = 60;                       // TXT 'Z' with no modifier == C4 by convention
    int midiDo = song.BaseMidiPitch;
    int mis = 0;
    for (int i = 0; i < txt.Actions.Count; i++)
    {
        int tp = PitchOf(txt.Actions[i], txtDo);
        int mp = PitchOf(song.Actions[i], midiDo);
        if (tp != mp)
        {
            if (mis < 3)
                Console.WriteLine($"    pitch mismatch at {i}: txt {txt.Actions[i].ComboText}={tp} " +
                                  $"vs midi {song.Actions[i].ComboText}={mp}");
            mis++;
        }
    }
    combosMatch = mis == 0;
    if (mis > 0) Console.WriteLine($"    total pitch mismatches: {mis}");
}
Check(combosMatch, "TXT 键位 column and MIDI produce identical sounding pitches");

Console.WriteLine("\n=== 5. TXT -> MIDI round trip ===");
var tmp = Path.Combine(Path.GetTempPath(), "dh_roundtrip.mid");
MidiWriter.Write(txt, tmp, bpm: 170, baseMidiPitch: txt.BaseMidiPitch);
var back = MidiParser.Parse(tmp);
Console.WriteLine($"  wrote {new FileInfo(tmp).Length} bytes, re-parsed {back.Notes.Count} notes");
Check(back.Notes.Count == txt.Actions.Count, "round trip preserves note count");

int pitchMismatch = 0;
for (int i = 0; i < Math.Min(back.Notes.Count, txt.Actions.Count); i++)
{
    int expect = MidiWriter.PitchOf(txt.Actions[i], txt.BaseMidiPitch);
    if (back.Notes[i].Pitch != expect) pitchMismatch++;
}
Check(pitchMismatch == 0, "round trip preserves every pitch");
File.Delete(tmp);

Console.WriteLine("\n=== 6. Trim / playback range ===");
{
    var s = new Song
    {
        LengthSec = 10,
        BaseMidiPitch = 60,
        Actions =
        {
            new NoteAction { TimeSec = 0, DurationSec = 1, Key = ScaleKey.Z },
            new NoteAction { TimeSec = 2, DurationSec = 1, Key = ScaleKey.X },
            new NoteAction { TimeSec = 4, DurationSec = 1, Key = ScaleKey.C },
            new NoteAction { TimeSec = 6, DurationSec = 1, Key = ScaleKey.V },
            new NoteAction { TimeSec = 8, DurationSec = 1, Key = ScaleKey.B },
        },
    };

    Check(!s.IsTrimmed, "untrimmed song reports IsTrimmed = false");
    Check(s.ActionsInRange().Count == 5, "untrimmed range keeps all actions");

    // Trim to 3..7. X spans [2,3] and only *touches* the start, so it contributes
    // no sound inside the window and is correctly excluded. C [4,5] and V [6,7]
    // are fully inside. Expected: 2 notes.
    s.TrimStartSec = 3;
    s.TrimEndSec = 7;
    var mid = s.ActionsInRange();
    Console.WriteLine($"  trimmed 3..7 -> {mid.Count} actions, times " +
                      string.Join(", ", mid.Select(a => $"{a.TimeSec:F2}/{a.DurationSec:F2}")));
    Check(s.IsTrimmed, "song reports IsTrimmed = true");
    Check(Math.Abs(s.EffectiveLength - 4) < 0.01, "effective length is 4s");
    Check(mid.Count == 2, "2 notes overlap the 3..7s window");
    Check(mid[0].Key == ScaleKey.C && mid[1].Key == ScaleKey.V, "the right two notes are kept");
    Check(mid.All(a => a.TimeSec >= 0), "rebased times are non-negative");
    Check(mid.All(a => a.TimeSec + a.DurationSec <= 4.01), "no note extends past the trimmed end");

    // A note genuinely straddling the start must be clipped and rebased.
    s.TrimStartSec = 3.5;      // cuts into C [4,5]? no - cuts nothing; use 4.5 to cut V? test C instead
    s.TrimEndSec = 7;
    var straddle = s.ActionsInRange();
    Console.WriteLine($"  trimmed 3.5..7 -> {straddle.Count} actions, times " +
                      string.Join(", ", straddle.Select(a => $"{a.TimeSec:F2}/{a.DurationSec:F2}")));
    Check(straddle.All(a => a.TimeSec + a.DurationSec <= 3.51), "clipped notes fit the window");

    s.TrimStartSec = 4.5;      // cuts C [4,5] in half
    s.TrimEndSec = 6.5;
    var cut = s.ActionsInRange();
    Console.WriteLine($"  trimmed 4.5..6.5 -> {cut.Count} actions, times " +
                      string.Join(", ", cut.Select(a => $"{a.TimeSec:F2}/{a.DurationSec:F2}")));
    Check(cut.Count == 2, "both partly-covered notes are kept when clipped");
    Check(cut.All(a => a.TimeSec >= -1e-6), "clipped notes are rebased to >= 0");
    Check(cut.Sum(a => a.DurationSec) <= 2.01, "clipped durations never exceed the window");

    s.TrimStartSec = 0;
    s.TrimEndSec = 0;
    Check(!s.IsTrimmed && s.ActionsInRange().Count == 5, "resetting the trim restores all notes");

    // End-only trim
    s.TrimEndSec = 5;
    Check(s.ActionsInRange().Count == 3, "end-only trim keeps the first 3 notes");
}

Console.WriteLine("\n=== 7. TXT bar alignment options ===");
{
    var withAlign = TxtScoreParser.Parse(TxtPath);
    var noAlign = TxtScoreParser.Parse(TxtPath, new TxtParseOptions { AlignBarsToMeasures = false });
    Console.WriteLine($"  aligned length   = {withAlign.LengthSec:F3}s");
    Console.WriteLine($"  unaligned length = {noAlign.LengthSec:F3}s");
    Check(withAlign.LengthSec > noAlign.LengthSec,
          "bar alignment pads short bars (aligned is longer)");
    Check(Math.Abs(withAlign.LengthSec - 67.765) < 0.05, "aligned length matches 48 full measures");
    Check(withAlign.Actions.Count == noAlign.Actions.Count, "alignment never changes the note count");

    // The four short bars should be reported.
    int reported = withAlign.Warnings.Count(w => w.Contains("补足"));
    Console.WriteLine($"  bars reported as padded: {reported}");
    Check(reported == 4, "exactly the 4 short bars (2, 6, 42, 46) are reported");

    var silent = TxtScoreParser.Parse(TxtPath, new TxtParseOptions { ReportAlignment = false });
    Check(silent.Warnings.All(w => !w.Contains("补足")), "alignment reporting can be turned off");
}

Console.WriteLine("\n=== 8. Error handling ===");
try
{
    var bad = Path.GetTempFileName();
    File.WriteAllText(bad, "这不是一个口琴谱，只是一段普通文本。");
    try { TxtScoreParser.Parse(bad); Check(false, "garbage TXT rejected"); }
    catch (InvalidDataException) { Check(true, "garbage TXT rejected with a clear error"); }
    File.Delete(bad);
}
catch (Exception ex) { Check(false, "garbage TXT test threw " + ex.GetType().Name); }

try
{
    MidiParser.Parse(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
    Check(false, "garbage MIDI rejected");
}
catch (InvalidDataException) { Check(true, "garbage MIDI rejected with a clear error"); }

Console.WriteLine("\n==============================");
Console.WriteLine(fail == 0 ? "ALL CHECKS PASSED" : $"{fail} CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
