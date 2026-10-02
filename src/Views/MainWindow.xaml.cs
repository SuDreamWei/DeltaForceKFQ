using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeltaHarmonica.Core;
using DeltaHarmonica.Interop;
using DeltaHarmonica.Models;
using DeltaHarmonica.Services;

namespace DeltaHarmonica.Views;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly PlaybackEngine _engine = new();
    private readonly AuditionPlayer _audition = new();
    private readonly HotkeyManager _hotkeys = new();
    private readonly InputLock _inputLock = new();
    private readonly ObservableCollection<Song> _songs = new();
    private readonly ObservableCollection<string> _recent = new();

    private Song? _pendingConvert;
    private TrayIcon? _tray;
    private bool _loaded;
    private bool _reallyClose;

    /// <summary>Playhead position in seconds, set by clicking the melody preview.</summary>
    private double _cursorSec;

    /// <summary>True while the user is dragging the playhead on the piano roll.</summary>
    private bool _scrubbingPreview;

    private bool _suppressTrimHandlers;

    private readonly Dictionary<HotkeyAction, HotkeyCaptureBox> _hotkeyBoxes = new();

    public MainWindow()
    {
        InitializeComponent();

        SongList.ItemsSource = _songs;
        RecentImportList.ItemsSource = _recent;

        _songs.CollectionChanged += (_, __) => UpdateListUi();
        _engine.StateChanged += OnEngineStateChanged;
        _engine.ProgressChanged += OnEngineProgress;
        _engine.StatusChanged += OnEngineStatus;
        _engine.NotePlayed += OnNotePlayed;

        _audition.ProgressChanged += (e, t) => Dispatcher.Invoke(() => OnAuditionProgress(e, t));
        _audition.Finished += () => Dispatcher.Invoke(OnAuditionFinished);

        SongList.SelectionChanged += (_, __) =>
        {
            if (!_loaded) return;
            var s = SongList.SelectedItem as Song;
            if (s is not null && s.LengthSec > 0.01)
                _cursorSec = Math.Clamp(_cursorSec, 0, s.LengthSec);
            UpdateConfigView();
            RedrawPreview();
        };

        PreviewCanvas.SizeChanged += (_, __) => RedrawPreview();

        Loaded += OnLoaded;
        Closing += OnClosing;

        AllowDrop = true;
        Drop += OnDrop;
        DragOver += (_, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };

        PreviewKeyDown += OnPreviewKeyDown;
    }

    // ==================================================================
    //  startup
    // ==================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hotkeys.Attach(this);
        _hotkeys.Triggered += OnHotkey;

        BuildHotkeyRows();
        RegisterHotkeys();

        LeadSlider.Value = _settings.LeadInMs;
        DurJitterSlider.Value = _settings.DurationJitterPercent;
        TimeJitterSlider.Value = _settings.TimingJitterMs;
        SpeedSlider.Value = _settings.Speed;
        HumaniseCheck.IsChecked = _settings.Humanise;
        TrayCheck.IsChecked = _settings.CloseToTray;
        LockInputCheck.IsChecked = _settings.LockInput;

        ApplySettingsToEngine();
        LoadSavedSongs();
        RefreshHotkeyHint();
        UpdateListUi();
        UpdateConfigView();

        _audition.MasterVolume = VolSlider.Value / 100.0;
        VolValue.Text = $"{(int)VolSlider.Value}%";
        TransposeValue.Text = "0";

        _tray = new TrayIcon(this);
        _tray.RestoreRequested += () => Dispatcher.Invoke(ToggleWindowVisible);

        _loaded = true;
        SetStatus($"就绪 · 已载入 {_songs.Count} 首歌曲");
    }

    private void LoadSavedSongs()
    {
        foreach (var path in _settings.SongPaths.ToList())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var song = LoadSongFromPath(path);
                if (_settings.Renames.TryGetValue(path, out var custom) && !string.IsNullOrWhiteSpace(custom))
                    song.Title = custom;
                _songs.Add(song);
            }
            catch
            {
                // Skip files that can no longer be parsed.
            }
        }
        SelectFirstIfAny();
    }

    private static Song LoadSongFromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".txt" => TxtScoreParser.Parse(path),
            ".mid" or ".midi" => LoadMidi(path),
            _ => throw new InvalidDataException($"不支持的文件类型：{ext}"),
        };
    }

    private static Song LoadMidi(string path)
    {
        var midi = MidiParser.Parse(path);
        var opt = new MapOptions { BaseMidiPitch = 60, AutoCentre = true, AllowSharp = true };
        var title = Path.GetFileNameWithoutExtension(path);
        return HarmonicaMapper.BuildSong(midi, opt, title, path);
    }

    // ==================================================================
    //  navigation
    // ==================================================================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;

        PageImport.Visibility = tag == "import" ? Visibility.Visible : Visibility.Collapsed;
        PageConvert.Visibility = tag == "convert" ? Visibility.Visible : Visibility.Collapsed;
        PageList.Visibility = tag == "list" ? Visibility.Visible : Visibility.Collapsed;
        PagePlay.Visibility = tag == "play" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        (PageTitle.Text, PageSubtitle.Text) = tag switch
        {
            "import" => ("选择音频", "导入 .mid / .midi 文件到候选列表"),
            "convert" => ("转换谱面", "把三角洲口琴谱 TXT 解析成可演奏的曲目，并可导出为 .mid"),
            "list" => ("候选列表", "调整顺序、重命名或移除歌曲"),
            "play" => ("演奏", "开始演奏并查看当前配置"),
            "settings" => ("设置", "全局热键、演奏参数与窗口行为"),
            _ => ("", ""),
        };

        if (tag == "play") UpdateConfigView();
    }

    private void UpdateListUi()
    {
        ListCountText.Text = _songs.Count.ToString();
        ListEmptyHint.Visibility = _songs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateConfigView();
    }

    // ==================================================================
    //  import
    // ==================================================================

    private void ImportMidi_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择 MIDI 或口琴谱文件",
            Filter = "MIDI 文件 (*.mid;*.midi)|*.mid;*.midi|口琴谱 TXT (*.txt)|*.txt|所有文件 (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        ImportFiles(dlg.FileNames);
    }

    private void ImportFiles(IEnumerable<string> paths)
    {
        int ok = 0;
        var errors = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                var song = LoadSongFromPath(path);
                _songs.Add(song);
                _settings.SongPaths.Add(path);

                var label = $"{song.Title}  ({song.SourceKind}, {song.NoteCountText})";
                _recent.Insert(0, label);
                while (_recent.Count > 8) _recent.RemoveAt(_recent.Count - 1);

                ok++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(path)}：{ex.Message}");
            }
        }

        _settings.Save();
        UpdateListUi();

        if (errors.Count > 0)
        {
            MessageBox.Show(this,
                $"成功导入 {ok} 个文件。\n\n以下文件失败：\n" + string.Join("\n", errors),
                "导入结果", MessageBoxButton.OK,
                errors.Count > 0 && ok == 0 ? MessageBoxImage.Error : MessageBoxImage.Warning);
        }

        SetStatus($"已导入 {ok} 个文件，候选列表共 {_songs.Count} 首");
        if (ok > 0) NavList.IsChecked = true;
    }

    private void OpenSongFolder_Click(object sender, RoutedEventArgs e)
    {
        var sel = SongList.SelectedItem as Song;
        var path = sel?.SourcePath;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            MessageBox.Show(this, "请先在候选列表里选中一首歌曲。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;

        var accepted = files.Where(f =>
        {
            var x = Path.GetExtension(f).ToLowerInvariant();
            return x is ".mid" or ".midi" or ".txt";
        }).ToArray();

        if (accepted.Length == 0)
        {
            MessageBox.Show(this, "只支持 .mid / .midi / .txt 文件。", "不支持的文件",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ImportFiles(accepted);
    }

    // ==================================================================
    //  convert
    // ==================================================================

    private void ConvertTxt_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择口琴谱 TXT",
            Filter = "口琴谱 TXT (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            _pendingConvert = TxtScoreParser.Parse(dlg.FileName);

            ConvertSummary.Text =
                $"《{_pendingConvert.Title}》\n" +
                $"音数：{_pendingConvert.Actions.Count}    时长：{_pendingConvert.DurationText}    " +
                $"升半音：{_pendingConvert.SharpNotes}    无法识别：{_pendingConvert.DroppedNotes}";

            ConvertWarnings.ItemsSource = _pendingConvert.Warnings;
            ConvertResultCard.Visibility = Visibility.Visible;
            ConvertAddBtn.IsEnabled = true;
            ConvertExportBtn.IsEnabled = true;

            SetStatus($"已解析 TXT：{_pendingConvert.Actions.Count} 个音");
        }
        catch (Exception ex)
        {
            _pendingConvert = null;
            ConvertResultCard.Visibility = Visibility.Collapsed;
            MessageBox.Show(this, "解析失败：\n\n" + ex.Message, "转换错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConvertAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingConvert is null) return;
        _songs.Add(_pendingConvert);
        _settings.SongPaths.Add(_pendingConvert.SourcePath);
        _settings.Save();
        UpdateListUi();
        SetStatus($"已加入候选列表：{_pendingConvert.Title}");
        NavList.IsChecked = true;
    }

    private void ConvertExport_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingConvert is null) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出为 MIDI",
            Filter = "MIDI 文件 (*.mid)|*.mid",
            FileName = _pendingConvert.Title + ".mid",
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            MidiWriter.Write(_pendingConvert, dlg.FileName,
                bpm: 120, baseMidiPitch: _pendingConvert.BaseMidiPitch);
            SetStatus($"已导出：{Path.GetFileName(dlg.FileName)}");

            var r = MessageBox.Show(this,
                "导出成功。\n\n要同时把它作为一个 MIDI 曲目加入候选列表吗？",
                "导出完成", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes)
            {
                var song = LoadMidi(dlg.FileName);
                _songs.Add(song);
                _settings.SongPaths.Add(dlg.FileName);
                _settings.Save();
                UpdateListUi();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导出失败：\n\n" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================================================================
    //  list management
    // ==================================================================

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);

    private void Move(int delta)
    {
        int i = SongList.SelectedIndex;
        if (i < 0) { SetStatus("请先选中一首歌曲"); return; }
        int j = i + delta;
        if (j < 0 || j >= _songs.Count) return;

        _songs.Move(i, j);
        SongList.SelectedIndex = j;
        PersistOrder();
    }

    private void PersistOrder()
    {
        _settings.SongPaths = _songs.Select(s => s.SourcePath).ToList();
        _settings.Save();
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItem is not Song song) { SetStatus("请先选中一首歌曲"); return; }

        var dialog = new RenameDialog(song.Title) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        song.Title = dialog.NewTitle;
        _settings.Renames[song.SourcePath] = dialog.NewTitle;
        _settings.Save();

        // Force the ListBox to rebuild the row so the new title shows.
        int idx = SongList.SelectedIndex;
        var items = SongList.ItemsSource;
        SongList.ItemsSource = null;
        SongList.ItemsSource = items;
        SongList.SelectedIndex = idx;

        UpdateConfigView();
        SetStatus($"已重命名为「{song.Title}」");
    }

    private void RemoveOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Song song) RemoveSong(song);
        e.Handled = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (SongList.SelectedItem is not Song song) { SetStatus("请先选中一首歌曲"); return; }
        RemoveSong(song);
    }

    private void RemoveSong(Song song)
    {
        if (_engine.CurrentSong == song) StopEverything();
        _songs.Remove(song);
        _settings.SongPaths.Remove(song.SourcePath);
        _settings.Save();
        SetStatus($"已移除「{song.Title}」");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_songs.Count == 0) return;
        var r = MessageBox.Show(this, $"确定要清空全部 {_songs.Count} 首歌曲吗？", "确认清空",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;

        StopEverything();
        _songs.Clear();
        _settings.SongPaths.Clear();
        _settings.Renames.Clear();
        _settings.Save();
        SetStatus("候选列表已清空");
    }

    private void SongList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SongList.SelectedItem is not Song song) return;
        NavPlay.IsChecked = true;
        UpdateConfigView();
        StartSong(song, skipLeadIn: false);
    }

    private void SelectFirstIfAny()
    {
        if (_songs.Count > 0 && SongList.SelectedIndex < 0) SongList.SelectedIndex = 0;
    }

    // ==================================================================
    //  playback
    // ==================================================================

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        var song = SongList.SelectedItem as Song ?? _engine.CurrentSong ?? _songs.FirstOrDefault();
        if (song is null)
        {
            MessageBox.Show(this, "候选列表是空的，请先导入歌曲。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartSong(song, skipLeadIn: false);
    }

    private void InstantPlay_Click(object sender, RoutedEventArgs e)
    {
        var song = SongList.SelectedItem as Song ?? _engine.CurrentSong ?? _songs.FirstOrDefault();
        if (song is null)
        {
            MessageBox.Show(this, "候选列表是空的，请先导入歌曲。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StartSong(song, skipLeadIn: true);
    }

    private void StartSong(Song song, bool skipLeadIn)
    {
        var actions = song.ActionsInRange();
        if (actions.Count == 0)
        {
            MessageBox.Show(this, "这首歌在选定区间里没有可演奏的音符。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StopEverything();
        SongList.SelectedItem = song;
        ApplySettingsToEngine();

        if (!skipLeadIn)
        {
            int lead = (int)LeadSlider.Value;
            var lockNote = LockInputCheck.IsChecked == true
                ? "\n\n演奏期间会锁定键鼠，只有紧急停止键仍然有效。"
                : "";
            var r = MessageBox.Show(this,
                $"即将演奏《{song.Title}》。\n\n" +
                $"共 {actions.Count} 个音，预计 {DescribeSeconds(song.EffectiveLength / Math.Max(0.1, _settings.Speed))}。\n\n" +
                $"请立刻切换到游戏窗口，并把鼠标停在不会误触的位置。{lockNote}\n\n" +
                $"你有 {lead / 1000.0:0.#} 秒准备时间。\n" +
                $"或者随时按 {_settings.InstantPlay.Describe()} 跳过等待立即开始。",
                "准备演奏", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;
        }
        else
        {
            SetStatus($"立即演奏《{song.Title}》…");
        }

        // Lock physical input just before the first note can fire, so a stray
        // click or keypress cannot disturb the octave/semitone modifiers.
        if (LockInputCheck.IsChecked == true)
        {
            _inputLock.ClearAllowedKeys();
            _inputLock.AllowKey(_settings.StopPlayback.VirtualKey);   // emergency exit
            _inputLock.AllowKey(_settings.ToggleWindow.VirtualKey);

            if (!_inputLock.Lock())
            {
                SetStatus("警告：键鼠锁定失败（可能被安全软件拦截），演奏仍会继续。");
                MessageBox.Show(this,
                    "键鼠锁定失败。\n\n可能是被安全软件拦截了低级钩子。\n" +
                    "演奏仍然可以进行，但请自己不要碰键鼠。",
                    "锁定失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        if (!_engine.Play(song, skipLeadIn))
        {
            _inputLock.Unlock();
            MessageBox.Show(this, "这首歌在选定区间里没有可演奏的音符。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        UpdateConfigView();
    }

    /// <summary>Play/pause the audible preview of the selection.</summary>
    private void Audition_Click(object sender, RoutedEventArgs e)
    {
        if (_audition.IsPlaying)
        {
            StopEverything();
            return;
        }

        var song = SongList.SelectedItem as Song ?? _engine.CurrentSong ?? _songs.FirstOrDefault();
        if (song is null)
        {
            MessageBox.Show(this, "先选一首歌再试听。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StopEverything();
        SongList.SelectedItem = song;
        _audition.MasterVolume = VolSlider.Value / 100.0;
        _audition.Play(song, _settings.Speed);
        UpdateConfigView();
    }

    private void PrevSong_Click(object sender, RoutedEventArgs e) => StepSong(-1);
    private void NextSong_Click(object sender, RoutedEventArgs e) => StepSong(1);

    private void StepSong(int delta)
    {
        if (_songs.Count == 0) return;
        int i = SongList.SelectedIndex;
        if (i < 0) i = 0;
        int j = ((i + delta) % _songs.Count + _songs.Count) % _songs.Count;

        StopEverything();
        var song = _songs[j];
        SongList.SelectedItem = song;
        SongList.ScrollIntoView(song);
        UpdateConfigView();
        SetStatus($"已选择：{song.Title}（{j + 1}/{_songs.Count}）");
    }

    private void VolSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _audition.MasterVolume = e.NewValue / 100.0;
        VolValue.Text = $"{(int)Math.Round(e.NewValue)}%";
    }

    private void TransposeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        int t = (int)Math.Round(e.NewValue);
        _audition.Transpose = t;
        TransposeValue.Text = t > 0 ? $"+{t}" : t.ToString();
    }

    private void OnAuditionProgress(double elapsed, double total)
    {
        if (total > 0.01) PlayProgress.Value = Math.Clamp(elapsed / total * 100.0, 0, 100);
        PlayStatus.Text = $"试听中… {DescribeSeconds(elapsed)} / {DescribeSeconds(total)}";
    }

    private void OnAuditionFinished()
    {
        if (_engine.IsBusy) return;
        PlayStatus.Text = "试听结束。";
        PlayProgress.Value = 0;
        UpdateConfigView();
    }

    private void OnNotePlayed(int index, int count)
    {
        Dispatcher.Invoke(() =>
        {
            var song = _engine.CurrentSong;
            if (song is null) return;
            var actions = song.ActionsInRange();
            if (index >= 0 && index < actions.Count)
                _cursorSec = actions[index].TimeSec + song.EffectiveStart;
        });
    }

    // ---------------------------------------------------------------- preview

    private const double PreviewLeftPad = 8;
    private const double PreviewRightPad = 8;
    private const double PreviewTopPad = 8;
    private const double PreviewBottomPad = 18;

    private double PreviewWidth => Math.Max(1, PreviewCanvas.ActualWidth - PreviewLeftPad - PreviewRightPad);
    private double PreviewHeight => Math.Max(1, PreviewCanvas.ActualHeight - PreviewTopPad - PreviewBottomPad);

    private static readonly Brush BarBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
    private static readonly Brush BarDimBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x5A, 0x38));
    private static readonly Brush GridBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3C));
    private static readonly Brush CursorBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0xBF, 0x7B));
    private static readonly Brush TrimBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xE8, 0xA3, 0x3D));

    /// <summary>Draw a compact piano roll: x = time, y = pitch, plus the trim window.</summary>
    private void RedrawPreview()
    {
        PreviewCanvas.Children.Clear();

        var song = SongList.SelectedItem as Song ?? _engine.CurrentSong;
        if (song is null || song.Actions.Count == 0)
        {
            PreviewHint.Text = "（未选择歌曲）";
            return;
        }

        double w = PreviewWidth, h = PreviewHeight;
        double total = Math.Max(0.01, song.LengthSec);

        // pitch extent across the whole song keeps the view stable while trimming
        int lo = int.MaxValue, hi = int.MinValue;
        foreach (var a in song.Actions)
        {
            int p = a.PitchWithBase(song.BaseMidiPitch);
            lo = Math.Min(lo, p);
            hi = Math.Max(hi, p);
        }
        if (hi <= lo) hi = lo + 1;
        int span = hi - lo;

        double YFor(int pitch) => PreviewTopPad + h - (pitch - lo) / (double)span * h;
        double XFor(double t) => PreviewLeftPad + Math.Clamp(t / total, 0, 1) * w;

        // faint horizontal guides
        for (int g = 0; g <= 4; g++)
        {
            double y = PreviewTopPad + h * g / 4.0;
            PreviewCanvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = PreviewLeftPad, X2 = PreviewLeftPad + w, Y1 = y, Y2 = y,
                Stroke = GridBrush, StrokeThickness = 1,
            });
        }

        double start = song.EffectiveStart, end = song.EffectiveEnd;

        // dim everything outside the trim range
        if (song.IsTrimmed)
        {
            double xs = XFor(start), xe = XFor(end);
            if (xs > PreviewLeftPad)
                PreviewCanvas.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = xs - PreviewLeftPad, Height = h,
                    Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x14, 0x1A)),
                }.At(PreviewLeftPad, PreviewTopPad));
            if (xe < PreviewLeftPad + w)
                PreviewCanvas.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = PreviewLeftPad + w - xe, Height = h,
                    Fill = new SolidColorBrush(Color.FromArgb(0xCC, 0x12, 0x14, 0x1A)),
                }.At(xe, PreviewTopPad));

            PreviewCanvas.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(0, xe - xs), Height = h, Fill = TrimBrush,
            }.At(xs, PreviewTopPad));
        }

        // notes
        double barMin = 3;
        foreach (var a in song.Actions)
        {
            int p = a.PitchWithBase(song.BaseMidiPitch);
            double x = XFor(a.TimeSec);
            double bw = Math.Max(barMin, XFor(a.TimeSec + a.DurationSec) - x);
            double y = YFor(p);
            bool inRange = a.TimeSec + a.DurationSec > start && a.TimeSec < end;

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = bw,
                Height = Math.Max(2.5, h / (span + 1) * 0.86),
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = inRange ? BarBrush : BarDimBrush,
                ToolTip = $"{DescribeSeconds(a.TimeSec)}  {a.ComboText}" +
                          (a.SourcePitch is null ? "" : $"  (简谱 {a.SourcePitch})"),
            };
            PreviewCanvas.Children.Add(rect.At(x, y - rect.Height / 2));
        }

        // playhead
        double cx = XFor(Math.Clamp(_cursorSec, 0, total));
        PreviewCanvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = cx, X2 = cx, Y1 = PreviewTopPad, Y2 = PreviewTopPad + h,
            Stroke = CursorBrush, StrokeThickness = 1.5,
        });

        // time axis labels
        foreach (var frac in new[] { 0.0, 0.25, 0.5, 0.75, 1.0 })
        {
            var tb = new TextBlock
            {
                Text = DescribeSeconds(total * frac),
                FontSize = 10,
                Foreground = (Brush)FindResource("TextDimBrush"),
            };
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x = PreviewLeftPad + w * frac - tb.DesiredSize.Width / 2;
            PreviewCanvas.Children.Add(tb.At(Math.Clamp(x, 0, PreviewLeftPad + w), PreviewTopPad + h + 2));
        }

        PreviewHint.Text =
            $"光标 {DescribeSeconds(_cursorSec)} · 区间 {DescribeSeconds(start)} ~ {DescribeSeconds(end)}" +
            $"（{DescribeSeconds(song.EffectiveLength)}）· 共 {song.Actions.Count} 个音";
    }

    private void PreviewCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var song = SongList.SelectedItem as Song ?? _engine.CurrentSong;
        if (song is null || song.Actions.Count == 0) return;

        _scrubbingPreview = true;
        PreviewCanvas.CaptureMouse();
        SetCursorFromPoint(e.GetPosition(PreviewCanvas), song);
    }

    private void PreviewCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_scrubbingPreview || e.LeftButton != MouseButtonState.Pressed) return;
        var song = SongList.SelectedItem as Song ?? _engine.CurrentSong;
        if (song is null || song.Actions.Count == 0) return;
        SetCursorFromPoint(e.GetPosition(PreviewCanvas), song);
    }

    private void PreviewCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_scrubbingPreview) return;
        _scrubbingPreview = false;
        PreviewCanvas.ReleaseMouseCapture();
    }

    /// <summary>Move the playhead to the time under the given canvas point.</summary>
    private void SetCursorFromPoint(Point pos, Song song)
    {
        double frac = Math.Clamp((pos.X - PreviewLeftPad) / PreviewWidth, 0, 1);
        _cursorSec = frac * Math.Max(0.01, song.LengthSec);
        RedrawPreview();
        SetStatus($"光标定位到 {DescribeSeconds(_cursorSec)}");
    }

    // ---------------------------------------------------------------- trim

    private void TrimStartSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _suppressTrimHandlers) return;
        var song = SongList.SelectedItem as Song;
        if (song is null) return;

        // Keep the start strictly before the end.
        if (TrimStartSlider.Value > TrimEndSlider.Value - 0.5)
        {
            _suppressTrimHandlers = true;
            TrimStartSlider.Value = Math.Max(0, TrimEndSlider.Value - 0.5);
            _suppressTrimHandlers = false;
            return;
        }

        song.TrimStartSec = Math.Clamp(TrimStartSlider.Value, 0, 100) / 100.0 * song.LengthSec;
        TrimStartValue.Text = DescribeSeconds(song.EffectiveStart);
        UpdateTrimInfo(song);
        RedrawPreview();
    }

    private void TrimEndSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _suppressTrimHandlers) return;
        var song = SongList.SelectedItem as Song;
        if (song is null) return;

        if (TrimEndSlider.Value < TrimStartSlider.Value + 0.5)
        {
            _suppressTrimHandlers = true;
            TrimEndSlider.Value = Math.Min(100, TrimStartSlider.Value + 0.5);
            _suppressTrimHandlers = false;
            return;
        }

        // 100% means "to the end", stored as 0 so a later reload stays correct.
        song.TrimEndSec = TrimEndSlider.Value >= 99.99
            ? 0
            : Math.Clamp(TrimEndSlider.Value, 0, 100) / 100.0 * song.LengthSec;
        TrimEndValue.Text = DescribeSeconds(song.EffectiveEnd);
        UpdateTrimInfo(song);
        RedrawPreview();
    }

    private void UpdateTrimInfo(Song song)
    {
        int n = song.ActionsInRange().Count;
        TrimInfo.Text = song.IsTrimmed
            ? $"区间内有 {n} 个音，时长 {DescribeSeconds(song.EffectiveLength)}"
            : "整首播放（未裁剪）";
    }

    private void TrimReset_Click(object sender, RoutedEventArgs e)
    {
        var song = SongList.SelectedItem as Song;
        if (song is null) return;
        song.TrimStartSec = 0;
        song.TrimEndSec = 0;
        SyncTrimSliders(song);
        SetStatus("播放区间已重置为整首");
    }

    private void TrimFromCursor_Click(object sender, RoutedEventArgs e)
    {
        var song = SongList.SelectedItem as Song;
        if (song is null) return;
        if (song.LengthSec <= 0.01) return;

        song.TrimStartSec = Math.Clamp(_cursorSec, 0, song.LengthSec);
        if (song.TrimEndSec > 0 && song.TrimEndSec <= song.TrimStartSec)
            song.TrimEndSec = 0;
        SyncTrimSliders(song);
        SetStatus($"播放起点设为 {DescribeSeconds(song.EffectiveStart)}");
    }

    /// <summary>Push the song's trim values into the sliders without re-entrancy.</summary>
    private void SyncTrimSliders(Song song)
    {
        double len = Math.Max(0.01, song.LengthSec);
        _suppressTrimHandlers = true;

        TrimStartSlider.Value = Math.Clamp(song.EffectiveStart / len * 100, 0, 100);
        TrimEndSlider.Value = song.TrimEndSec > 0
            ? Math.Clamp(song.TrimEndSec / len * 100, 0, 100)
            : 100;

        _suppressTrimHandlers = false;

        TrimStartValue.Text = DescribeSeconds(song.EffectiveStart);
        TrimEndValue.Text = DescribeSeconds(song.EffectiveEnd);
        UpdateTrimInfo(song);
        RedrawPreview();
    }

    private static string DescribeSeconds(double sec)
    {
        if (sec < 0) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _engine.TogglePause();

    private void Stop_Click(object sender, RoutedEventArgs e) => StopEverything();

    private void OnEngineStateChanged(PlaybackState state)
    {
        Dispatcher.Invoke(() =>
        {
            bool busy = state != PlaybackState.Idle;
            PlayBtn.IsEnabled = !busy;
            PauseBtn.IsEnabled = state is PlaybackState.Playing or PlaybackState.Paused;
            StopBtn.IsEnabled = busy;
            PauseBtn.Content = state == PlaybackState.Paused ? "▶  继续" : "⏸  暂停";
        });
    }

    private void OnEngineProgress(double elapsed, double total)
    {
        Dispatcher.Invoke(() =>
        {
            if (total > 0.01)
                PlayProgress.Value = Math.Clamp(elapsed / total * 100.0, 0, 100);
        });
    }

    private void OnEngineStatus(string text)
    {
        Dispatcher.Invoke(() =>
        {
            PlayStatus.Text = text;
            StatusText.Text = text;
            if (text == "已结束") PlayProgress.Value = 100;
        });
    }

    private void UpdateConfigView()
    {
        var song = _engine.CurrentSong ?? SongList.SelectedItem as Song;
        NowPlayingTitle.Text = song?.Title ?? "（未选择）";
        NowPlayingMeta.Text = song is null
            ? "从候选列表里选择一首，或直接双击列表中的歌曲。"
            : $"{song.SourceKind} · {song.NoteCountText} · {song.DurationText}" +
              $" · 基准 do = {HarmonicaMapper.PitchName(song.BaseMidiPitch)}" +
              $" · BPM {song.Bpm:0.#}" +
              (song.IsTrimmed ? $" · 已裁剪 {DescribeSeconds(song.EffectiveLength)}" : "");

        CfgLead.Text = _settings.SkipLeadIn
            ? "已跳过（立即演奏）"
            : $"{_settings.LeadInMs} 毫秒";
        CfgJitter.Text = _settings.Humanise
            ? $"开启（时长 ±{_settings.DurationJitterPercent}%，间隔 0~{_settings.TimingJitterMs} 毫秒）"
            : "关闭";
        CfgSpeed.Text = $"{_settings.Speed:0.00}×" +
                        (song is not null
                            ? $"（{DescribeSeconds(song.EffectiveLength / Math.Max(0.1, _settings.Speed))}）"
                            : "");
        CfgHotkeys.Text =
            $"{_settings.InstantPlay.Describe()} 立即演奏 · " +
            $"{_settings.NextSong.Describe()} 下一首 · " +
            $"{_settings.Restart.Describe()} 重播 · " +
            $"{_settings.StopPlayback.Describe()} 停止 · " +
            $"{_settings.ToggleWindow.Describe()} 显示/隐藏";

        AuditionBtn.Content = _audition.IsPlaying ? "⏹  停止试听" : "♪  试听";
        LockStateHint.Text = _inputLock.IsLocked
            ? $"● 键鼠已锁定（已拦截 {_inputLock.BlockedCount} 次输入）"
            : "○ 键鼠未锁定";
        LockStateHint.Foreground = _inputLock.IsLocked
            ? (Brush)FindResource("OkBrush")
            : (Brush)FindResource("TextDimBrush");

        if (song is not null)
        {
            PreviewListFallback.Text = song.Actions
                .Take(40)
                .Select(a => $"{a.TimeSec,6:0.00}s  {a.ComboText}")
                .Aggregate(string.Empty, (acc, s) => acc + (acc.Length > 0 ? "   " : "") + s);
        }
        else PreviewListFallback.Text = string.Empty;

        if (song is not null && _loaded) SyncTrimSliders(song);
    }

    // ==================================================================
    //  settings page
    // ==================================================================

    private void BuildHotkeyRows()
    {
        var rows = new (string Label, HotkeyAction Action, Func<HotkeyConfig> Get)[]
        {
            ("立即演奏（跳过等待）", HotkeyAction.InstantPlay, () => _settings.InstantPlay),
            ("下一首歌曲", HotkeyAction.NextSong, () => _settings.NextSong),
            ("重新播放当前歌曲", HotkeyAction.Restart, () => _settings.Restart),
            ("停止播放", HotkeyAction.StopPlayback, () => _settings.StopPlayback),
            ("显示 / 隐藏窗口", HotkeyAction.ToggleWindow, () => _settings.ToggleWindow),
        };

        for (int i = 0; i < rows.Length; i++)
        {
            var (label, action, get) = rows[i];

            var lbl = new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 6),
            };
            lbl.SetResourceReference(TextBlock.StyleProperty, "Body");

            var box = new HotkeyCaptureBox(get())
            {
                Margin = new Thickness(0, 5, 10, 5),
                Height = 34,
            };
            box.Captured += (mods, vk) => OnHotkeyCaptured(action, mods, vk);
            _hotkeyBoxes[action] = box;

            var clear = new Button
            {
                Content = "恢复默认",
                Margin = new Thickness(0, 5, 0, 5),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            clear.SetResourceReference(FrameworkElement.StyleProperty, "GhostButton");
            clear.Click += (_, __) => ResetHotkey(action);

            Grid.SetRow(lbl, i); Grid.SetColumn(lbl, 0);
            Grid.SetRow(box, i); Grid.SetColumn(box, 1);
            Grid.SetRow(clear, i); Grid.SetColumn(clear, 2);
            HotkeyGrid.Children.Add(lbl);
            HotkeyGrid.Children.Add(box);
            HotkeyGrid.Children.Add(clear);
        }
    }

    private void OnHotkeyCaptured(HotkeyAction action, uint mods, uint vk)
    {
        if (mods == 0 && vk is >= 0x70 and <= 0x87)
        {
            SetStatus($"{HotkeyBinding.KeyName(vk)} 没有修饰键，可能与其他程序冲突");
        }

        var cfg = ConfigFor(action);
        cfg.Modifiers = mods;
        cfg.VirtualKey = vk;
        _settings.Save();

        RegisterHotkeys();
        RefreshHotkeyHint();
        UpdateConfigView();
        SetStatus($"热键已更新：{cfg.Describe()}");
    }

    private void ResetHotkey(HotkeyAction action)
    {
        var cfg = ConfigFor(action);
        (cfg.Modifiers, cfg.VirtualKey) = action switch
        {
            HotkeyAction.InstantPlay => (HotkeyManager.MOD_SHIFT, 0x71u),
            HotkeyAction.NextSong => (HotkeyManager.MOD_SHIFT, 0x76u),
            HotkeyAction.Restart => (HotkeyManager.MOD_SHIFT, 0x77u),
            HotkeyAction.StopPlayback => (HotkeyManager.MOD_SHIFT, 0x78u),
            HotkeyAction.ToggleWindow => (HotkeyManager.MOD_SHIFT, 0x70u),
            _ => (HotkeyManager.MOD_SHIFT, 0x70u),
        };
        _hotkeyBoxes[action].SetBinding(cfg);
        _settings.Save();
        RegisterHotkeys();
        RefreshHotkeyHint();
        UpdateConfigView();
    }

    private HotkeyConfig ConfigFor(HotkeyAction a) => a switch
    {
        HotkeyAction.InstantPlay => _settings.InstantPlay,
        HotkeyAction.NextSong => _settings.NextSong,
        HotkeyAction.Restart => _settings.Restart,
        HotkeyAction.StopPlayback => _settings.StopPlayback,
        HotkeyAction.ToggleWindow => _settings.ToggleWindow,
        _ => _settings.ToggleWindow,
    };

    private void RegisterHotkeys()
    {
        _hotkeys.Failures.Clear();
        _hotkeys.Register(HotkeyAction.InstantPlay, _settings.InstantPlay.Modifiers, _settings.InstantPlay.VirtualKey);
        _hotkeys.Register(HotkeyAction.NextSong, _settings.NextSong.Modifiers, _settings.NextSong.VirtualKey);
        _hotkeys.Register(HotkeyAction.Restart, _settings.Restart.Modifiers, _settings.Restart.VirtualKey);
        _hotkeys.Register(HotkeyAction.StopPlayback, _settings.StopPlayback.Modifiers, _settings.StopPlayback.VirtualKey);
        if (!_hotkeys.Register(HotkeyAction.ToggleWindow, _settings.ToggleWindow.Modifiers, _settings.ToggleWindow.VirtualKey))
            _hotkeys.Register(HotkeyAction.ToggleWindow, HotkeyManager.MOD_SHIFT, 0x70);

        if (_hotkeys.Failures.Count > 0)
            SetStatus("以下热键注册失败（可能被其他程序占用）：" + string.Join("、", _hotkeys.Failures));
    }

    private void RefreshHotkeyHint()
    {
        HotkeyHint.Text =
            $"{_settings.ToggleWindow.Describe()} 显示/隐藏   " +
            $"{_settings.InstantPlay.Describe()} 立即演奏   " +
            $"{_settings.NextSong.Describe()} 下一首   " +
            $"{_settings.Restart.Describe()} 重播   " +
            $"{_settings.StopPlayback.Describe()} 停止";
    }

    // ---- hotkey actions ----

    /// <summary>
    /// The song a hotkey should act on: whatever is selected in the candidate
    /// list, falling back to the last-played song, then the first entry.
    ///
    /// The list selection must win. Preferring the last-played song made
    /// "Shift+F2 play now" ignore the user's choice and replay an old song.
    /// </summary>
    private Song? TargetSong()
        => SongList.SelectedItem as Song
           ?? _engine.CurrentSong
           ?? _songs.FirstOrDefault();

    private void OnHotkey(HotkeyAction action)
    {
        Dispatcher.Invoke(() =>
        {
            switch (action)
            {
                case HotkeyAction.ToggleWindow:
                    ToggleWindowVisible();
                    break;

                case HotkeyAction.StopPlayback:
                    StopEverything();
                    SetStatus("已通过热键停止播放");
                    break;

                case HotkeyAction.InstantPlay:
                {
                    var target = TargetSong();
                    if (target is null) { SetStatus("候选列表为空"); break; }
                    StartSong(target, skipLeadIn: true);
                    break;
                }

                case HotkeyAction.Restart:
                {
                    // "重新播放" must mean the song in front of the user, and it must
                    // go through the same start path as everything else so the old
                    // run is fully retired first (otherwise two threads emit at once
                    // and the game receives random key combinations).
                    var target = TargetSong();
                    if (target is null) { SetStatus("候选列表为空"); break; }
                    StartSong(target, skipLeadIn: true);
                    SetStatus($"重新播放《{target.Title}》");
                    break;
                }

                case HotkeyAction.NextSong:
                    if (_songs.Count == 0) { SetStatus("候选列表为空"); break; }
                    int i = SongList.SelectedIndex;
                    if (i < 0) i = _engine.CurrentSong is { } c2 ? _songs.IndexOf(c2) : -1;
                    int next = ((i + 1) % _songs.Count + _songs.Count) % _songs.Count;
                    var song = _songs[next];
                    SongList.SelectedItem = song;
                    SongList.ScrollIntoView(song);
                    SetStatus($"切到下一首：{song.Title}（{next + 1}/{_songs.Count}）");
                    UpdateConfigView();
                    break;
            }
        });
    }

    /// <summary>Stop both the audition and the performance, and release input.</summary>
    private void StopEverything()
    {
        _audition.Stop();
        _engine.Stop();
        _inputLock.Unlock();
    }

    private void ToggleWindowVisible()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            Hide();
            _tray?.SetVisible(true);
        }
        else
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }
    }

    // ==================================================================
    //  settings sliders
    // ==================================================================

    private void ApplySettingsToEngine()
    {
        _engine.Settings.LeadInMs = _settings.LeadInMs;
        _engine.Settings.DurationJitterPercent = _settings.DurationJitterPercent;
        _engine.Settings.TimingJitterMs = _settings.TimingJitterMs;
        _engine.Settings.Humanise = _settings.Humanise;
        _engine.Settings.Speed = _settings.Speed;
    }

    private void LeadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _settings.LeadInMs = (int)Math.Round(e.NewValue);
        LeadValue.Text = $"{_settings.LeadInMs} 毫秒";
        ApplySettingsToEngine();
        _settings.Save();
        UpdateConfigView();
    }

    private void DurJitterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _settings.DurationJitterPercent = (int)Math.Round(e.NewValue);
        DurJitterValue.Text = $"±{_settings.DurationJitterPercent}%";
        ApplySettingsToEngine();
        _settings.Save();
        UpdateConfigView();
    }

    private void TimeJitterSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _settings.TimingJitterMs = (int)Math.Round(e.NewValue);
        TimeJitterValue.Text = $"{_settings.TimingJitterMs} 毫秒";
        ApplySettingsToEngine();
        _settings.Save();
        UpdateConfigView();
    }

    private void SpeedSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded) return;
        _settings.Speed = Math.Round(e.NewValue, 2);
        SpeedValue.Text = $"{_settings.Speed:0.00}×";
        ApplySettingsToEngine();
        _settings.Save();
        UpdateConfigView();
    }

    /// <summary>Speed quick-set buttons: 0.5x, 0.8x, 1.0x, 1.2x, 1.5x, 2.0x.</summary>
    private void SpeedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;
        if (!double.TryParse(tag, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out var v)) return;

        SpeedSlider.Value = v;             // the ValueChanged handler applies it
        SetStatus($"演奏速度设为 {v:0.00}×");
    }

    private void LockInput_Click(object sender, RoutedEventArgs e)
    {
        _settings.LockInput = LockInputCheck.IsChecked == true;
        _settings.Save();

        // Turning the option off mid-performance must release the lock immediately.
        if (!_settings.LockInput && _inputLock.IsLocked)
        {
            _inputLock.Unlock();
            SetStatus("键鼠锁定已解除");
        }
    }

    private void Humanise_Click(object sender, RoutedEventArgs e)
    {
        _settings.Humanise = HumaniseCheck.IsChecked == true;
        ApplySettingsToEngine();
        _settings.Save();
        UpdateConfigView();
    }

    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        _settings.CloseToTray = TrayCheck.IsChecked == true;
        _settings.Save();
    }

    // ==================================================================
    //  self test
    // ==================================================================

    private void SelfTest_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show(this,
            "将依次发送以下输入，用来验证按键与鼠标事件是否生效：\n\n" +
            "Z X C V B N M ,\n" +
            "然后依次带上 [中键] [右键] [左键]\n\n" +
            "请在 3 秒内点开记事本或其他可输入文本的窗口。\n" +
            "注意：会真的按下鼠标左中右键，请先把鼠标移到空白处。\n\n继续吗？",
            "输入自检", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (r != MessageBoxResult.OK) return;

        Task.Run(() =>
        {
            Thread.Sleep(3000);
            int[] vks = { 0x5A, 0x58, 0x43, 0x56, 0x42, 0x4E, 0x4D, 0xBC };
            foreach (var vk in vks)
            {
                Interop.InputSimulator.KeyDown(vk);
                Thread.Sleep(60);
                Interop.InputSimulator.KeyUp(vk);
            }

            void Combo(Interop.InputSimulator.MouseButton b)
            {
                Thread.Sleep(150);
                Interop.InputSimulator.MouseDown(b);
                Thread.Sleep(60);
                Interop.InputSimulator.KeyDown(0x58);
                Thread.Sleep(60);
                Interop.InputSimulator.KeyUp(0x58);
                Thread.Sleep(60);
                Interop.InputSimulator.MouseUp(b);
            }

            Combo(Interop.InputSimulator.MouseButton.Middle);
            Combo(Interop.InputSimulator.MouseButton.Right);
            Combo(Interop.InputSimulator.MouseButton.Left);
            PlaybackEngine.ReleaseEverything();
        });

        SetStatus("输入自检已启动，3 秒后开始发送…");
    }

    // ==================================================================
    //  misc
    // ==================================================================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Allow Esc to cancel an in-progress hotkey capture.
        if (e.Key == Key.Escape)
        {
            foreach (var box in _hotkeyBoxes.Values)
                if (box.IsCapturing) { box.CancelCapture(); e.Handled = true; return; }
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_reallyClose && _settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.SetVisible(true);
            _tray?.ShowBalloon("程序仍在后台运行", $"按 {_settings.ToggleWindow.Describe()} 可以重新打开窗口。");
            return;
        }

        _reallyClose = true;
        StopEverything();
        _inputLock.Dispose();
        _hotkeys.Dispose();
        _tray?.Dispose();
        _settings.Save();
    }

    public void ExitApplication()
    {
        _reallyClose = true;
        Close();
        Application.Current.Shutdown();
    }

    private void SetStatus(string text) => StatusText.Text = text;
}
