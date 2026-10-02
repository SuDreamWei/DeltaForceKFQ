using System.Text.Json.Serialization;

namespace DeltaHarmonica.Models;

/// <summary>Which of Z X C V B N M , is pressed. 0 = Z (do) .. 7 = ',' (high do).</summary>
public enum ScaleKey { Z = 0, X = 1, C = 2, V = 3, B = 4, N = 5, M = 6, Comma = 7 }

/// <summary>
/// One physical action the game receives: hold the octave/semitone mouse
/// modifiers, tap the scale key for a duration, release the modifiers.
/// </summary>
public sealed class NoteAction
{
    public double TimeSec { get; set; }
    public double DurationSec { get; set; }
    public ScaleKey Key { get; set; }

    /// <summary>Negative = hold left mouse (octave down), positive = right mouse (octave up).</summary>
    public int OctaveShift { get; set; }

    /// <summary>Hold middle mouse (semitone up).</summary>
    public bool Sharp { get; set; }

    // ---- provenance, for the UI preview / diagnostics ----
    public string? SourcePitch { get; set; }

    public string KeyText => Key switch
    {
        ScaleKey.Z => "Z", ScaleKey.X => "X", ScaleKey.C => "C", ScaleKey.V => "V",
        ScaleKey.B => "B", ScaleKey.N => "N", ScaleKey.M => "M", ScaleKey.Comma => ",",
        _ => "?"
    };

    /// <summary>Human readable combination, e.g. "[左键][中键]X".</summary>
    public string ComboText
    {
        get
        {
            var s = string.Empty;
            if (OctaveShift < 0) s += "[左键]";
            if (OctaveShift > 0) s += "[右键]";
            if (Sharp) s += "[中键]";
            return s + KeyText;
        }
    }

    /// <summary>
    /// Sounding pitch, given the song's base "do". Used for the piano-roll preview
    /// and for the audible audition synth.
    /// </summary>
    public int PitchWithBase(int baseMidiPitch)
    {
        int[] major = { 0, 2, 4, 5, 7, 9, 11, 12 };
        return baseMidiPitch + OctaveShift * 12 + major[(int)Key] + (Sharp ? 1 : 0);
    }

    public NoteAction Clone() => new()
    {
        TimeSec = TimeSec,
        DurationSec = DurationSec,
        Key = Key,
        OctaveShift = OctaveShift,
        Sharp = Sharp,
        SourcePitch = SourcePitch,
    };
}

/// <summary>A song ready to be played, from any source.</summary>
public sealed class Song
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "未命名";
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>"MIDI" or "TXT" - shown as a badge.</summary>
    public string SourceKind { get; set; } = "MIDI";

    public double LengthSec { get; set; }
    public List<NoteAction> Actions { get; set; } = new();

    /// <summary>Warnings produced while loading (dropped notes, folded octaves...).</summary>
    public List<string> Warnings { get; set; } = new();

    public int DroppedNotes { get; set; }
    public int FoldedNotes { get; set; }
    public int SharpNotes { get; set; }
    public int BaseMidiPitch { get; set; } = 60;

    /// <summary>Tempo in BPM when known (TXT header, or the MIDI's initial tempo).</summary>
    public double Bpm { get; set; } = 120;

    // ---- playback range (trim), in seconds; 0/full means "whole song" ----
    public double TrimStartSec { get; set; }
    public double TrimEndSec { get; set; }

    [JsonIgnore]
    public bool IsTrimmed => TrimStartSec > 0.0001 ||
                             (TrimEndSec > 0.0001 && TrimEndSec < LengthSec - 0.0001);

    [JsonIgnore]
    public double EffectiveStart => Math.Max(0, TrimStartSec);

    [JsonIgnore]
    public double EffectiveEnd =>
        TrimEndSec > 0.0001 ? Math.Min(TrimEndSec, LengthSec) : LengthSec;

    [JsonIgnore]
    public double EffectiveLength => Math.Max(0, EffectiveEnd - EffectiveStart);

    /// <summary>The actions inside the active range, with times rebased to zero.</summary>
    public List<NoteAction> ActionsInRange()
    {
        if (!IsTrimmed)
            return Actions;

        double s = EffectiveStart, e = EffectiveEnd;
        var list = new List<NoteAction>();
        foreach (var a in Actions)
        {
            if (a.TimeSec >= e) break;
            if (a.TimeSec + a.DurationSec <= s) continue;

            var c = a.Clone();
            // Clamp the note to the range, then rebase so playback starts at 0.
            double noteStart = Math.Max(a.TimeSec, s);
            double noteEnd = Math.Min(a.TimeSec + a.DurationSec, e);
            c.TimeSec = noteStart - s;
            c.DurationSec = Math.Max(0.02, noteEnd - noteStart);
            list.Add(c);
        }
        return list;
    }

    [JsonIgnore]
    public string DurationText
    {
        get
        {
            var t = TimeSpan.FromSeconds(EffectiveLength);
            return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
        }
    }

    [JsonIgnore]
    public string NoteCountText => $"{Actions.Count} 个音";

    [JsonIgnore]
    public string BadgeText => IsTrimmed ? SourceKind + "✂" : SourceKind;

    public override string ToString() => Title;
}
