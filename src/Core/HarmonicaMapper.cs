using System.IO;
using DeltaHarmonica.Models;

namespace DeltaHarmonica.Core;

public sealed class MapOptions
{
    /// <summary>MIDI pitch that plain 'Z' (no mouse modifier) produces.</summary>
    public int BaseMidiPitch { get; set; } = 60;
    public int MaxOctaveShift { get; set; } = 2;
    public bool AllowSharp { get; set; } = true;
    public bool AutoCentre { get; set; } = true;
}

/// <summary>
/// Converts absolute MIDI pitches into harmonica key actions.
/// Mirrors the verified C++ implementation.
/// </summary>
public static class HarmonicaMapper
{
    private static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11, 12 };

    public static bool MapPitch(int midiPitch, MapOptions opt,
                                out ScaleKey key, out int octaveShift, out bool sharp)
    {
        key = ScaleKey.Z; octaveShift = 0; sharp = false;

        int rel = midiPitch - opt.BaseMidiPitch;
        int octave = (int)Math.Floor(rel / 12.0);
        int semi = rel - octave * 12;
        if (semi < 0) { semi += 12; octave--; }

        for (int d = 0; d < 8; d++)
        {
            if (MajorScale[d] == semi)
            {
                if (octave < -opt.MaxOctaveShift || octave > opt.MaxOctaveShift) return false;
                key = (ScaleKey)d; octaveShift = octave; sharp = false;
                return true;
            }
        }

        if (opt.AllowSharp)
        {
            for (int d = 0; d < 8; d++)
            {
                if (MajorScale[d] + 1 == semi)
                {
                    if (octave < -opt.MaxOctaveShift || octave > opt.MaxOctaveShift) return false;
                    key = (ScaleKey)d; octaveShift = octave; sharp = true;
                    return true;
                }
            }
        }

        int bestD = -1, bestDist = 99;
        for (int d = 0; d < 8; d++)
        {
            int dist = semi - MajorScale[d];
            if (dist >= 0 && dist < bestDist) { bestDist = dist; bestD = d; }
        }
        if (bestD < 0) { bestD = 7; }
        if (octave < -opt.MaxOctaveShift || octave > opt.MaxOctaveShift) return false;
        key = (ScaleKey)bestD; octaveShift = octave; sharp = false;
        return true;
    }

    /// <summary>Build a playable song from parsed MIDI notes.</summary>
    public static Song BuildSong(MidiParseResult midi, MapOptions opt, string title, string path)
    {
        var song = new Song
        {
            Title = title,
            SourcePath = path,
            SourceKind = "MIDI",
        };

        var src = midi.Notes;
        if (src.Count == 0) throw new InvalidDataException("这个 MIDI 里没有任何音符");

        int lo = src.Min(n => n.Pitch);
        int hi = src.Max(n => n.Pitch);

        if (opt.AutoCentre)
        {
            int bestBase = opt.BaseMidiPitch;
            long bestCost = -1;
            int loBase = Math.Max(lo - 12, 12), hiBase = Math.Min(hi + 12, 115);
            for (int candidate = loBase; candidate <= hiBase; candidate++)
            {
                long cost = 0;
                var probe = new MapOptions
                {
                    BaseMidiPitch = candidate,
                    MaxOctaveShift = opt.MaxOctaveShift,
                    AllowSharp = opt.AllowSharp,
                    AutoCentre = false,
                };
                foreach (var n in src)
                {
                    if (!MapPitch(n.Pitch, probe, out _, out var os, out var sh))
                        cost += 1000;
                    else
                        cost += (long)Math.Abs(os) * Math.Abs(os) * 10 + (sh ? 1 : 0);
                }
                if (bestCost < 0 || cost < bestCost) { bestCost = cost; bestBase = candidate; }
            }
            opt.BaseMidiPitch = bestBase;
        }

        song.BaseMidiPitch = opt.BaseMidiPitch;

        var actions = new List<NoteAction>(src.Count);
        foreach (var n in src)
        {
            if (!MapPitch(n.Pitch, opt, out var key, out var os, out var sh))
            {
                song.DroppedNotes++;
                continue;
            }

            int outPitch = opt.BaseMidiPitch + os * 12 + MajorScale[(int)key] + (sh ? 1 : 0);
            if (outPitch != n.Pitch) song.FoldedNotes++;
            if (sh) song.SharpNotes++;

            actions.Add(new NoteAction
            {
                TimeSec = n.TimeSec,
                DurationSec = n.DurationSec,
                Key = key,
                OctaveShift = os,
                Sharp = sh,
                SourcePitch = PitchName(n.Pitch),
            });
        }

        Sanitize(actions);
        song.Actions = actions;
        song.LengthSec = midi.LengthSec;
        if (actions.Count > 0)
            song.LengthSec = Math.Max(song.LengthSec, actions.Max(a => a.TimeSec + a.DurationSec));

        song.LengthSec = Math.Max(song.LengthSec, midi.LengthSec);

        song.Warnings.Add($"MIDI：{src.Count} 个音，原始音域 {PitchName(lo)} ~ {PitchName(hi)}，时长 {song.DurationText}");
        if (song.FoldedNotes > 0)
            song.Warnings.Add($"有 {song.FoldedNotes} 个音超出音域，已自动折叠八度（基准 do = {PitchName(opt.BaseMidiPitch)}）");
        if (song.SharpNotes > 0)
            song.Warnings.Add($"有 {song.SharpNotes} 个音需要按住鼠标中键升半音");
        if (song.DroppedNotes > 0)
            song.Warnings.Add($"有 {song.DroppedNotes} 个音无法映射，已丢弃");

        return song;
    }

    /// <summary>
    /// Ensure no two actions land on the same physical key while it is still held,
    /// and clamp durations to something a human could actually press.
    /// </summary>
    public static void Sanitize(List<NoteAction> actions)
    {
        actions.Sort((a, b) => a.TimeSec.CompareTo(b.TimeSec));

        const double MinDur = 0.02;
        foreach (var a in actions)
            if (a.DurationSec < MinDur) a.DurationSec = MinDur;

        for (int i = 0; i < actions.Count; i++)
        {
            var cur = actions[i];
            for (int j = i + 1; j < actions.Count; j++)
            {
                var nxt = actions[j];
                if (nxt.TimeSec >= cur.TimeSec + cur.DurationSec) break;
                if (nxt.Key == cur.Key && nxt.OctaveShift == cur.OctaveShift && nxt.Sharp == cur.Sharp)
                {
                    const double gap = 0.015;
                    double avail = nxt.TimeSec - cur.TimeSec - gap;
                    if (avail > 0.01 && avail < cur.DurationSec) cur.DurationSec = avail;
                }
            }
        }
    }

    public static string PitchName(int midi)
    {
        if (midi < 0 || midi > 127) return "--";
        string[] names = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        return $"{names[midi % 12]}{midi / 12 - 1}";
    }
}
