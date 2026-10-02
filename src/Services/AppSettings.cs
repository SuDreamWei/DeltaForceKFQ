using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeltaHarmonica.Services;

public sealed class HotkeyConfig
{
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }

    public string Describe()
    {
        var parts = new List<string>();
        if ((Modifiers & HotkeyManager.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyManager.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyManager.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyManager.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(HotkeyBinding.KeyName(VirtualKey));
        return string.Join("+", parts);
    }
}

public sealed class AppSettings
{
    public List<string> SongPaths { get; set; } = new();
    public Dictionary<string, string> Renames { get; set; } = new();

    public HotkeyConfig NextSong { get; set; } = new() { Modifiers = HotkeyManager.MOD_SHIFT, VirtualKey = 0x76 }; // Shift+F7
    public HotkeyConfig Restart { get; set; } = new() { Modifiers = HotkeyManager.MOD_SHIFT, VirtualKey = 0x77 }; // Shift+F8
    public HotkeyConfig StopPlayback { get; set; } = new() { Modifiers = HotkeyManager.MOD_SHIFT, VirtualKey = 0x78 }; // Shift+F9
    public HotkeyConfig ToggleWindow { get; set; } = new() { Modifiers = HotkeyManager.MOD_SHIFT, VirtualKey = 0x70 }; // Shift+F1
    public HotkeyConfig InstantPlay { get; set; } = new() { Modifiers = HotkeyManager.MOD_SHIFT, VirtualKey = 0x71 }; // Shift+F2

    public int LeadInMs { get; set; } = 3000;

    /// <summary>When set, "开始演奏" also skips the countdown (same as Shift+F2).</summary>
    public bool SkipLeadIn { get; set; }

    /// <summary>Swallow physical keyboard/mouse input while performing.</summary>
    public bool LockInput { get; set; } = true;
    public int DurationJitterPercent { get; set; } = 12;
    public int TimingJitterMs { get; set; } = 8;
    public bool Humanise { get; set; } = true;
    public double Speed { get; set; } = 1.0;
    public int BaseMidiPitch { get; set; } = 60;
    public bool AutoCentre { get; set; } = true;

    /// <summary>Minimise to tray instead of closing.</summary>
    public bool CloseToTray { get; set; } = true;

    // ------------------------------------------------------------------

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DeltaHarmonica");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json, Opts);
                if (s is not null) return s;
            }
        }
        catch
        {
            // Corrupt settings should never prevent the app from starting.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
