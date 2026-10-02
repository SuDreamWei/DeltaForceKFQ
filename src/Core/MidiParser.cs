using System.IO;
using System.Text;

namespace DeltaHarmonica.Core;

public sealed record RawNote(double TimeSec, double DurationSec, int Pitch, int Velocity, int Track);

public sealed class MidiParseResult
{
    public int Format { get; set; }
    public int Division { get; set; }
    public string Title { get; set; } = string.Empty;
    public double LengthSec { get; set; }
    public List<RawNote> Notes { get; set; } = new();
}

/// <summary>
/// Minimal Standard MIDI File reader. Supports format 0 and 1.
/// Handles running status, variable-length quantities, tempo meta events and
/// both PPQ and SMPTE time divisions.
/// </summary>
public static class MidiParser
{
    public static MidiParseResult Parse(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes);
    }

    public static MidiParseResult Parse(byte[] b)
    {
        if (b.Length < 14) throw new InvalidDataException("文件太小，不是有效的 MIDI 文件");

        int p = 0;
        if (ReadU32(b, ref p) != 0x4D546864) throw new InvalidDataException("缺少 MThd 头，不是 MIDI 文件");
        uint hdrLen = ReadU32(b, ref p);
        if (hdrLen < 6) throw new InvalidDataException("MThd 头长度异常");

        var res = new MidiParseResult
        {
            Format = ReadU16(b, ref p),
            Division = 0,
        };
        int nTracks = ReadU16(b, ref p);
        int division = ReadU16(b, ref p);
        p += (int)hdrLen - 6;
        if (p > b.Length) throw new InvalidDataException("文件被截断");

        if (res.Format == 2)
            throw new InvalidDataException("不支持 MIDI format 2（多独立序列），请另存为 format 0 或 1");

        bool smpte = (division & 0x8000) != 0;
        double secPerTick = 0;
        if (smpte)
        {
            int fps = 256 - ((division >> 8) & 0xFF);
            int tpf = division & 0xFF;
            if (fps <= 0 || tpf <= 0) throw new InvalidDataException("SMPTE 时间基准无效");
            secPerTick = 1.0 / (fps * (double)tpf);
            res.Division = 480;
        }
        else
        {
            if (division <= 0) throw new InvalidDataException("每拍 tick 数为 0，文件无效");
            res.Division = division;
        }

        double tempoUsPerQuarter = 500000.0;
        double maxEnd = 0;

        for (int t = 0; t < nTracks && p + 8 <= b.Length; t++)
        {
            uint id = ReadU32(b, ref p);
            uint len = ReadU32(b, ref p);
            int trackEnd = (int)Math.Min(p + (long)len, b.Length);
            if (id != 0x4D54726B) { p = trackEnd; continue; }

            byte running = 0;
            uint abstick = 0;
            double sec = 0;

            var open = new (double Start, int Vel, bool Used)[16, 128];

            while (p < trackEnd)
            {
                uint delta = ReadVarLen(b, ref p, trackEnd);
                abstick += delta;
                sec += smpte
                    ? delta * secPerTick
                    : delta * (tempoUsPerQuarter / 1e6) / res.Division;

                if (p >= trackEnd) break;
                byte status = b[p];
                if ((status & 0x80) != 0) { p++; running = status; }
                else status = running;
                if (status < 0x80) break;

                byte type = (byte)(status & 0xF0);
                int chan = status & 0x0F;

                if (type == 0x80 || type == 0x90)
                {
                    int pitch = ReadByte(b, ref p, trackEnd);
                    int vel = ReadByte(b, ref p, trackEnd);
                    if (pitch > 127) pitch = 127;
                    bool on = type == 0x90 && vel > 0;

                    var slot = open[chan, pitch];
                    if (on)
                    {
                        if (slot.Used)
                        {
                            double d = sec - slot.Start;
                            if (d > 1e-4)
                                res.Notes.Add(new RawNote(slot.Start, d, pitch, slot.Vel, t));
                            maxEnd = Math.Max(maxEnd, sec);
                        }
                        open[chan, pitch] = (sec, vel, true);
                    }
                    else if (slot.Used)
                    {
                        double d = sec - slot.Start;
                        open[chan, pitch] = (slot.Start, slot.Vel, false);
                        if (d > 1e-4)
                            res.Notes.Add(new RawNote(slot.Start, d, pitch, slot.Vel, t));
                    }
                    maxEnd = Math.Max(maxEnd, sec);
                }
                else if (type is 0xA0 or 0xB0 or 0xE0)
                {
                    p += 2; if (p > trackEnd) p = trackEnd;
                }
                else if (type is 0xC0 or 0xD0)
                {
                    p += 1; if (p > trackEnd) p = trackEnd;
                }
                else if (status == 0xFF)
                {
                    byte meta = (byte)ReadByte(b, ref p, trackEnd);
                    uint mlen = ReadVarLen(b, ref p, trackEnd);
                    int mend = (int)Math.Min(p + (long)mlen, trackEnd);

                    if (meta == 0x51 && mlen == 3 && p + 3 <= trackEnd)
                    {
                        tempoUsPerQuarter = (b[p] << 16) | (b[p + 1] << 8) | b[p + 2];
                        if (tempoUsPerQuarter <= 0) tempoUsPerQuarter = 500000.0;
                    }
                    else if (meta == 0x03 && res.Title.Length == 0 && mlen > 0)
                    {
                        var sb = new StringBuilder();
                        for (int k = p; k < mend && k < p + 64; k++)
                        {
                            byte ch = b[k];
                            if (ch == 0) break;
                            sb.Append(ch is >= 32 and < 127 ? (char)ch : ' ');
                        }
                        var s = sb.ToString().Trim();
                        if (s.Length > 0) res.Title = s;
                    }
                    p = mend;
                }
                else if (status is 0xF0 or 0xF7)
                {
                    uint slen = ReadVarLen(b, ref p, trackEnd);
                    p += (int)slen;
                    if (p > trackEnd) p = trackEnd;
                }
                else break;
            }
            p = trackEnd;
        }

        foreach (var n in res.Notes) maxEnd = Math.Max(maxEnd, n.TimeSec + n.DurationSec);

        res.Notes.Sort((x, y) => x.TimeSec.CompareTo(y.TimeSec));
        res.LengthSec = maxEnd;

        if (res.Notes.Count == 0) throw new InvalidDataException("这个 MIDI 里没有任何音符");
        return res;
    }

    private static uint ReadU32(byte[] b, ref int p)
    {
        if (p + 4 > b.Length) { p = b.Length; return 0; }
        uint v = (uint)((b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
        p += 4; return v;
    }

    private static int ReadU16(byte[] b, ref int p)
    {
        if (p + 2 > b.Length) { p = b.Length; return 0; }
        int v = (b[p] << 8) | b[p + 1];
        p += 2; return v;
    }

    private static int ReadByte(byte[] b, ref int p, int end)
    {
        if (p >= end || p >= b.Length) { p = end; return 0; }
        return b[p++];
    }

    private static uint ReadVarLen(byte[] b, ref int p, int end)
    {
        uint v = 0;
        for (int i = 0; i < 4; i++)
        {
            if (p >= end || p >= b.Length) break;
            byte c = b[p++];
            v = (v << 7) | (uint)(c & 0x7F);
            if ((c & 0x80) == 0) break;
        }
        return v;
    }
}
