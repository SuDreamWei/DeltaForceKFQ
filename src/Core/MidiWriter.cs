using System.IO;
using DeltaHarmonica.Models;

namespace DeltaHarmonica.Core;

/// <summary>
/// Writes a Standard MIDI File (format 1) from a parsed song, so a TXT score can
/// be exported as .mid. Two tracks: a tempo/title track and a note track with a
/// GM program change so it is audible in any player.
/// </summary>
public static class MidiWriter
{
    /// <summary>Pitch that a ScaleKey + octave shift + sharp resolves to.</summary>
    public static int PitchOf(NoteAction a, int baseMidiPitch)
    {
        int[] major = { 0, 2, 4, 5, 7, 9, 11, 12 };
        return baseMidiPitch + a.OctaveShift * 12 + major[(int)a.Key] + (a.Sharp ? 1 : 0);
    }

    public static void Write(Song song, string path, double bpm = 120, int baseMidiPitch = 60)
    {
        const int Tpqn = 480;
        var bytes = new List<byte>();

        // ---------- MThd ----------
        bytes.AddRange("MThd"u8.ToArray());
        WriteU32(bytes, 6);
        WriteU16(bytes, 1);            // format 1
        WriteU16(bytes, 2);            // two tracks
        WriteU16(bytes, Tpqn);

        // ---------- track 0: tempo + name ----------
        var trk0 = new List<byte>();
        // track name
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(song.Title);
        WriteVarLen(trk0, 0);
        trk0.AddRange(new byte[] { 0xFF, 0x03 });
        WriteVarLen(trk0, (uint)Math.Min(nameBytes.Length, 127));
        trk0.AddRange(nameBytes.Take(127));
        // tempo
        uint usPerQuarter = (uint)Math.Round(60_000_000.0 / Math.Max(1, bpm));
        WriteVarLen(trk0, 0);
        trk0.AddRange(new byte[] { 0xFF, 0x51, 0x03 });
        trk0.Add((byte)((usPerQuarter >> 16) & 0xFF));
        trk0.Add((byte)((usPerQuarter >> 8) & 0xFF));
        trk0.Add((byte)(usPerQuarter & 0xFF));
        // time signature 4/4
        WriteVarLen(trk0, 0);
        trk0.AddRange(new byte[] { 0xFF, 0x58, 0x04, 0x04, 0x02, 0x18, 0x08 });
        // end of track
        WriteVarLen(trk0, 0);
        trk0.AddRange(new byte[] { 0xFF, 0x2F, 0x00 });

        bytes.AddRange("MTrk"u8.ToArray());
        WriteU32(bytes, (uint)trk0.Count);
        bytes.AddRange(trk0);

        // ---------- track 1: notes ----------
        var trk1 = new List<byte>();
        // Program change -> Acoustic Grand Piano (channel 0)
        WriteVarLen(trk1, 0);
        trk1.AddRange(new byte[] { 0xC0, 0x00 });

        var events = new List<(double Tick, bool On, int Pitch, int Vel)>();
        foreach (var a in song.Actions)
        {
            int pitch = PitchOf(a, baseMidiPitch);
            if (pitch is < 0 or > 127) continue;
            double startTick = a.TimeSec * bpm / 60.0 * Tpqn;
            double endTick = (a.TimeSec + a.DurationSec) * bpm / 60.0 * Tpqn;

            // A repeated note on the same pitch must not have its note-off land on
            // exactly the next note-on tick: many readers (including ours) would
            // merge the two into one long note. Pull the note-off one tick earlier
            // so back-to-back repeats stay distinct.
            endTick = Math.Floor(endTick);
            if (Math.Abs(endTick - startTick) < 1) endTick = startTick + 1;

            events.Add((Math.Floor(startTick), true, pitch, 100));
            events.Add((endTick, false, pitch, 0));
        }

        // Note-offs before note-ons at the same tick, so a repeated pitch closes
        // its previous instance first.
        events.Sort((x, y) =>
        {
            int c = x.Tick.CompareTo(y.Tick);
            if (c != 0) return c;
            return x.On.CompareTo(y.On);
        });

        // Guarantee strict ordering for identical pitch at identical tick.
        for (int i = 1; i < events.Count; i++)
        {
            if (events[i].Tick == events[i - 1].Tick &&
                events[i].Pitch == events[i - 1].Pitch &&
                !events[i].On && events[i - 1].On)
            {
                var e = events[i];
                e.Tick += 1;
                events[i] = e;
            }
        }
        events.Sort((x, y) =>
        {
            int c = x.Tick.CompareTo(y.Tick);
            if (c != 0) return c;
            return x.On.CompareTo(y.On);
        });

        long last = 0;
        foreach (var e in events)
        {
            long tick = (long)Math.Round(e.Tick);
            long delta = tick - last;
            if (delta < 0) delta = 0;
            last = tick;
            WriteVarLen(trk1, (uint)delta);
            trk1.Add((byte)(e.On ? 0x90 : 0x80));
            trk1.Add((byte)e.Pitch);
            trk1.Add((byte)e.Vel);
        }

        WriteVarLen(trk1, 0);
        trk1.AddRange(new byte[] { 0xFF, 0x2F, 0x00 });

        bytes.AddRange("MTrk"u8.ToArray());
        WriteU32(bytes, (uint)trk1.Count);
        bytes.AddRange(trk1);

        File.WriteAllBytes(path, bytes.ToArray());
    }

    private static void WriteU32(List<byte> b, uint v)
    {
        b.Add((byte)((v >> 24) & 0xFF));
        b.Add((byte)((v >> 16) & 0xFF));
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)(v & 0xFF));
    }

    private static void WriteU16(List<byte> b, int v)
    {
        b.Add((byte)((v >> 8) & 0xFF));
        b.Add((byte)(v & 0xFF));
    }

    private static void WriteVarLen(List<byte> b, uint v)
    {
        Span<byte> tmp = stackalloc byte[4];
        int n = 0;
        do { tmp[n++] = (byte)(v & 0x7F); v >>= 7; } while (v > 0);
        for (int i = n - 1; i >= 0; i--)
            b.Add((byte)(tmp[i] | (i > 0 ? 0x80 : 0)));
    }
}
