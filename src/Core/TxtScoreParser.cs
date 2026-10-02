using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using DeltaHarmonica.Models;

namespace DeltaHarmonica.Core;

/// <summary>Knobs for TXT score interpretation.</summary>
public sealed class TxtParseOptions
{
    /// <summary>
    /// Pad each bar out to a whole number of measures (implicit trailing rests).
    /// This is standard notation and correct for essentially every score; it is
    /// exposed as an option only so a badly-formed file can be played literally.
    /// </summary>
    public bool AlignBarsToMeasures { get; set; } = true;

    /// <summary>Emit a warning for bars that needed noticeable padding.</summary>
    public bool ReportAlignment { get; set; } = true;

    /// <summary>Override the BPM from the file header. 0 = use the file's value.</summary>
    public double BpmOverride { get; set; } = 0;
}

/// <summary>
/// Parser for the Delta Force harmonica TXT score format.
///
/// Layout of a bar block:
///     小节   1 (4/4)
///       简谱  3' 3' 3' 3' 6
///       键位  C+ C+ C+ C+ N
///       节奏  4  4  8  8  4
///
/// The 键位 (key) column is authoritative: it already encodes the exact keys and
/// mouse modifiers a player would press, so we use it directly instead of
/// re-deriving the mapping from the 简谱 column. The 简谱 column is used as a
/// cross-check and for display.
///
/// Key marks:
///     '+' = 升调  (mouse right button, octave up)
///     '-' = 降调  (mouse left button,  octave down)
///     '#' = 半音  (mouse middle button, semitone up)
///
/// Rhythm column: note value in the usual numeric notation 1/2/4/8/16, with an
/// optional trailing dot for a dotted note. 4/4 time, so value V spans V/4 beats.
/// </summary>
public static class TxtScoreParser
{
    private static readonly Regex BarHeader = new(
        @"^\s*小节\s*(\d+)\s*(?:\((\d+)\s*/\s*(\d+)\))?",
        RegexOptions.Compiled);

    private static readonly Regex BpmLine = new(
        @"BPM\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Leading label of a data line inside a bar: 简谱 / 键位 / 节奏.</summary>
    private static readonly Regex KeyLine = new(@"^\s*键位\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex RhythmLine = new(@"^\s*节奏\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex DegreeLine = new(@"^\s*简谱\s*(.*)$", RegexOptions.Compiled);

    public static Song Parse(string path, TxtParseOptions? options = null)
    {
        var text = File.ReadAllText(path, DetectEncoding(path));
        return ParseText(text, path, options);
    }

    /// <summary>Pick UTF-8 when the bytes are valid UTF-8, else fall back to GB18030.</summary>
    private static Encoding DetectEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(true);
        try
        {
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return new UTF8Encoding(false);
        }
        catch (DecoderFallbackException)
        {
            try { return Encoding.GetEncoding("GB18030"); }
            catch { return Encoding.Default; }
        }
    }

    public static Song ParseText(string text, string path, TxtParseOptions? options = null)
    {
        options ??= new TxtParseOptions();
        var song = new Song
        {
            Title = Path.GetFileNameWithoutExtension(path),
            SourcePath = path,
            SourceKind = "TXT",
        };

        double bpm = 120;
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        // Title: first non-empty line that is not a metadata line.
        foreach (var raw in lines)
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (BpmLine.IsMatch(l) && l.Contains('·')) break;   // it's the meta line
            if (l.StartsWith("键位标记")) continue;
            if (BarHeader.IsMatch(l)) break;
            song.Title = l;
            break;
        }

        var bpmMatch = BpmLine.Match(text);
        if (bpmMatch.Success &&
            double.TryParse(bpmMatch.Groups[1].Value, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out var parsedBpm) && parsedBpm > 0)
            bpm = parsedBpm;

        if (options.BpmOverride > 0) bpm = options.BpmOverride;

        // Record the tempo actually used so the UI and the MIDI exporter agree.
        song.Bpm = bpm;

        // ---- walk the bars ----
        int i = 0;
        double cursorSec = 0;
        double beatSec = 60.0 / bpm;
        int barsSeen = 0;

        while (i < lines.Length)
        {
            var header = BarHeader.Match(lines[i]);
            if (!header.Success) { i++; continue; }

            barsSeen++;
            int beatsPerBar = 4;
            if (header.Groups[2].Success &&
                int.TryParse(header.Groups[2].Value, out var num) &&
                int.TryParse(header.Groups[3].Value, out var den) && den > 0)
                beatsPerBar = (int)Math.Round(num * 4.0 / den);

            // Gather the 简谱 / 键位 / 节奏 lines belonging to this bar.
            string? keysRaw = null, rhythmRaw = null, degreeRaw = null;
            int j = i + 1;
            for (; j < lines.Length; j++)
            {
                if (BarHeader.IsMatch(lines[j])) break;
                var kl = KeyLine.Match(lines[j]);
                if (kl.Success) { keysRaw = kl.Groups[1].Value; continue; }
                var rl = RhythmLine.Match(lines[j]);
                if (rl.Success) { rhythmRaw = rl.Groups[1].Value; continue; }
                var dl = DegreeLine.Match(lines[j]);
                if (dl.Success) { degreeRaw = dl.Groups[1].Value; continue; }
            }
            i = j;

            var keyTokens = Tokenize(keysRaw);
            var rhythmTokens = Tokenize(rhythmRaw);
            var degreeTokens = Tokenize(degreeRaw);

            if (keyTokens.Count == 0) continue;

            if (keyTokens.Count != rhythmTokens.Count)
            {
                song.Warnings.Add(
                    $"第 {header.Groups[1].Value} 小节：键位数({keyTokens.Count}) 与节奏数({rhythmTokens.Count}) 不匹配，" +
                    "已按较少的一个处理。");
            }

            int n = Math.Min(keyTokens.Count, rhythmTokens.Count);
            if (n == 0) n = keyTokens.Count;

            // Remember where this bar starts and how much time its notes actually
            // consume, so we can pad short bars out to a full measure afterwards.
            double barStart = cursorSec;
            double barNoteSec = 0;

            for (int k = 0; k < n; k++)
            {
                var keyTok = keyTokens[k];
                double beats = k < rhythmTokens.Count ? RhythmBeats(rhythmTokens[k]) : 1.0;
                double durSec = beats * beatSec;
                barNoteSec += durSec;

                if (TryParseKey(keyTok, out var key, out var octave, out var sharp))
                {
                    song.Actions.Add(new NoteAction
                    {
                        TimeSec = cursorSec,
                        DurationSec = durSec,
                        Key = key,
                        OctaveShift = octave,
                        Sharp = sharp,
                        SourcePitch = k < degreeTokens.Count ? degreeTokens[k] : null,
                    });
                }
                else
                {
                    song.DroppedNotes++;
                    song.Warnings.Add($"第 {header.Groups[1].Value} 小节第 {k + 1} 个音：无法识别的键位标记 \"{keyTok}\"");
                }

                cursorSec += durSec;
            }

            // ---- bar-line alignment -------------------------------------------
            // A bar in N/4 occupies exactly `beatsPerBar` beats. If the notes in
            // this bar do not fill it, the remainder is a rest: the score's author
            // simply did not write the rest out. Advancing the cursor to the next
            // bar line is standard music notation, not a MIDI-specific tweak - it
            // applies to any TXT score, and without it every short bar drags the
            // following bars earlier by the missing fraction.
            //
            // Round to the nearest whole number of measures rather than always
            // rounding up: a bar may legitimately be written across two measures,
            // and an over-long bar should not be pushed even further out.
            if (options.AlignBarsToMeasures)
            {
                double measureSec = beatsPerBar * beatSec;
                if (measureSec > 1e-9)
                {
                    double barBeats = (cursorSec - barStart) / beatSec;
                    double wholeBars = Math.Round(barBeats / beatsPerBar,
                                                  MidpointRounding.AwayFromZero);
                    if (wholeBars < 1) wholeBars = 1;

                    double shortfallBeats = wholeBars * beatsPerBar - barBeats;
                    if (shortfallBeats > 0.001)
                    {
                        cursorSec = barStart + wholeBars * measureSec;
                        if (options.ReportAlignment && shortfallBeats >= 0.24)
                        {
                            song.Warnings.Add(
                                $"第 {header.Groups[1].Value} 小节只写了 {barBeats:0.##} 拍" +
                                $"（4/4 应为 {beatsPerBar} 拍），已按休止符补足 {shortfallBeats:0.##} 拍。");
                        }
                    }
                    else if (barBeats > wholeBars * beatsPerBar + 0.001)
                    {
                        // Notes spilled past the bar line: keep them where they are
                        // rather than truncating real music, but say so.
                        song.Warnings.Add(
                            $"第 {header.Groups[1].Value} 小节的音符共 {barBeats:0.##} 拍，" +
                            $"超出了 {wholeBars} 个小节，已保留原始时长未做裁剪。");
                    }
                }
            }

            song.SharpNotes += song.Actions.Count(a => a.Sharp);
        }

        if (barsSeen == 0 && song.Actions.Count == 0)
            throw new InvalidDataException(
                "没有解析到任何小节。请确认这是「三角洲口琴谱」TXT，格式应包含「小节 / 键位 / 节奏」三行。");

        if (song.Actions.Count == 0)
            throw new InvalidDataException("解析到小节结构，但没有任何有效音符。");

        song.LengthSec = song.Actions.Count > 0
            ? song.Actions.Max(a => a.TimeSec + a.DurationSec)
            : 0;

        song.Warnings.Insert(0,
            $"TXT 口琴谱：{barsSeen} 小节，{song.Actions.Count} 个音，BPM {bpm:0.##}，时长 {song.DurationText}");
        return song;
    }

    /// <summary>Split a column into tokens, tolerating irregular spacing.</summary>
    private static List<string> Tokenize(string? s)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return list;
        foreach (var t in s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            list.Add(t);
        return list;
    }

    /// <summary>
    /// Convert a rhythm token into a length in beats.
    ///
    /// The column holds note-value denominators: 4 = quarter, 8 = eighth, 16 =
    /// sixteenth. In 4/4 a quarter note is one beat, so beats = 4 / value.
    /// A trailing dot lengthens the note by half, so a dotted quarter (4·) is
    /// 1.5 beats. Doing the dot arithmetic on the beat count (not the denominator)
    /// keeps this correct - dividing the denominator would shorten the note.
    /// Returns 1 beat for anything unrecognisable.
    /// </summary>
    private static double RhythmBeats(string token)
    {
        var t = token.Trim();
        bool dotted = false;
        while (t.EndsWith("·") || t.EndsWith("."))
        {
            dotted = true;
            t = t[..^1];
        }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0)
            return 1.0;

        double beats = 4.0 / v;
        if (dotted) beats *= 1.5;
        return beats;
    }

    /// <summary>
    /// Parse a key token like "C+", "X", ",-", "N#". The trailing marks combine:
    /// '+' octave up, '-' octave down, '#' semitone up.
    /// </summary>
    private static bool TryParseKey(string token, out ScaleKey key, out int octave, out bool sharp)
    {
        key = ScaleKey.Z;
        octave = 0;
        sharp = false;

        if (string.IsNullOrEmpty(token)) return false;

        char head = char.ToUpperInvariant(token[0]);
        key = head switch
        {
            'Z' => ScaleKey.Z,
            'X' => ScaleKey.X,
            'C' => ScaleKey.C,
            'V' => ScaleKey.V,
            'B' => ScaleKey.B,
            'N' => ScaleKey.N,
            'M' => ScaleKey.M,
            ',' => ScaleKey.Comma,
            _ => (ScaleKey)(-1),
        };
        if ((int)key < 0) return false;

        for (int i = 1; i < token.Length; i++)
        {
            switch (token[i])
            {
                case '+': octave += 1; break;
                case '-': octave -= 1; break;
                case '#': sharp = true; break;
                case '·': case '.': break;          // stray separator, ignore
                default: return false;
            }
        }
        return true;
    }
}
