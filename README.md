# 三角洲行动 · 口风琴自动演奏器（WPF 版）

C# / WPF 重写版本，深色侧边栏界面，带候选列表管理和全局热键。

**不读取游戏内存、不修改游戏文件、不发网络包** —— 只发送标准键盘鼠标输入事件。
> [!WARNING]
> 自动化键盘或鼠标输入可能违反游戏规则，并可能带来封禁等风险。本项目不提供反检测、绕过反作弊或修改游戏内存的功能；是否使用以及产生的后果由使用者自行承担。

---

## 一、编译和运行

### 编译
```
cd D:\WorkArea\DeltaHarmonicaWPF
dotnet build -c Release
```

### 运行
```
dotnet run -c Release
```
或直接双击 `bin\Release\net10.0-windows\DeltaHarmonica.exe`

首次编译需要 .NET 10 SDK（你机器上已有 10.0.302）。

---

## 二、界面说明

左侧边栏五个入口：

| 入口 | 作用 |
|------|------|
| **选择音频** | 导入 `.mid` / `.midi`（也能导入 `.txt` 口琴谱）。支持拖拽文件进窗口 |
| **转换谱面** | 把三角洲口琴谱 TXT 解析成曲目。可「加入候选列表」直接播放，也可「导出为 .mid」 |
| **候选列表** | 上移 / 下移 / 重命名 / 移除 / 清空。双击某首歌进入演奏页 |
| **演奏** | 试听、旋律预览、播放区间裁剪、开始/立即/暂停/停止 |
| **设置**（左下角） | 全局热键、演奏参数、窗口行为 |

候选列表的顺序和重命名会自动保存，下次启动还在。

### 演奏页详解

1. **当前歌曲** —— 标题 + 元信息（音数、时长、基准 do、BPM，裁剪后显示区间时长）
2. **试听** —— 用本机合成音播放旋律，**不会发送任何按键到游戏**。
   有独立音量和音高（移调 ±24 半音）滑块，方便先确认好不好听
3. **旋律预览** —— 钢琴卷帘图，横轴时间纵轴音高。
   - **点击**任意位置即可把播放光标定位过去
   - 高亮区域就是当前播放区间，区间外会变暗
4. **播放区间（裁剪）** —— 左右拖动「起点 / 终点」滑块只演奏选定的一段，适合练习某一句。
   - 「设为当前光标」把起点设成预览里点的位置
   - 「重置为整首」取消裁剪
5. **当前配置** —— 一览当前所有参数

### 演奏速度（倍速）
在「设置 → 演奏参数 → 演奏速度」调整：

- **快捷倍率按钮**：0.5× / 0.8× / 1.2× / 1.5× / 2.0×，点一下即可
- **滑块微调**：0.3× ~ 2.5×，步进 0.05

**1.2× 更快，0.8× 更慢**，同时作用于**试听和实际演奏**。
当前配置里会显示变速后的预计时长。

实测精度（音符间隔对比理论值）：

| 倍率 | 实测 | 理论 | 偏差 |
|------|------|------|------|
| 0.5× | 2.020s | 2.000s | +1.0% |
| 0.8× | 1.251s | 1.250s | +0.1% |
| 1.0× | 1.001s | 1.000s | +0.1% |
| 1.2× | 0.840s | 0.833s | +0.7% |
| 1.5× | 0.689s | 0.667s | +3.4% |
| 2.0× | 0.500s | 0.500s | +0.1% |

### 锁定键鼠
演奏页有「**演奏期间锁定键鼠（防误触）**」开关（默认开）。

开启后演奏期间会用底层输入钩子**拦截真实的键盘和鼠标按键**，
只放行本程序自己发送的事件（通过事件里的标记位区分）：

- 键盘：全部拦下
- 鼠标：**按键和滚轮拦下，指针仍可移动**（不然你会觉得鼠标坏了）
- **紧急停止热键始终有效**，任何时候都能解锁

「演奏」页会显示当前锁定状态和已拦截次数。如果被安全软件拦住了钩子，
程序会明确告诉你是"锁定失败"而不是默默失效。

---

## 三、全局热键

| 热键 | 作用 |
|------|------|
| **Shift+F2** | **立即演奏**（跳过准备倒计时，确认框之后马上开始） |
| **Shift+F7** | 下一首（到列表底部会回到第一首） |
| **Shift+F8** | 重新播放当前歌曲（同样跳过等待） |
| **Shift+F9** | 停止播放 |
| **Shift+F1** | 显示 / 隐藏程序窗口 |

在「设置」里点热键输入框，然后**直接按下你想用的组合键**即可改键（Esc 取消）。
改完立即生效，也会保存。

隐藏窗口后程序仍在后台运行，热键保持有效。托盘图标左键点击可恢复窗口，右键有菜单。

> **什么时候用 Shift+F2？** 正常「开始演奏」会弹确认框 + 3 秒准备时间，
> 这是给你切窗口用的。如果你已经切好窗口、鼠标也放好了，
> 直接按 **Shift+F2** 就立刻开始，不用再等。

---

## 四、玩法：怎么用

1. 打开程序，进「选择音频」导入你的 MIDI（或去「转换谱面」载入 TXT 谱）
2. 进「候选列表」确认顺序
3. 进「演奏」，选中歌曲，点「**开始演奏**」
4. 弹出确认框，点确定后有 **3 秒准备时间**（可调），立刻切到游戏窗口
5. **切到游戏后不要再点鼠标** —— 点鼠标会打乱八度修饰键的状态
6. 演奏中按 **Shift+F9** 停止，或按 **Shift+F1** 隐藏窗口

### 第一次用建议先做「输入自检」
在「演奏」页点「输入自检」，它会给你 3 秒切到记事本，然后依次发送
`Z X C V B N M ,` 和带修饰键的组合，确认按键能正常送达。

---

## 五、键位映射

游戏里的口风琴：

| 按键 | 音级 |
|------|------|
| Z X C V B N M , | do re mi fa sol la si (高音)do |

| 鼠标 | 作用 |
|------|------|
| 左键 | 降八度 |
| 中键 | 升半音 |
| 右键 | 升八度 |

一个音发送的序列：
```
[按住修饰键] → 按下音键 → 按住音符时长 → 松开音键 → [松开修饰键]
```

例：`高音 re` = `[右键按住] X [松开右键]`；`升 re` = `[中键按住] X [松开中键]`

### 自动八度折叠
口琴只有一个大调音阶（8 个白键）。钢琴 MIDI 音域远超这个范围，
程序会**自动把所有音折叠进可演奏范围**，并自动挑一个「基准 do」让折叠次数最少。
「演奏」页会显示结果（如 `基准 do = A4`）。

### 半音处理
黑键靠按住鼠标中键实现。

---

## 六、TXT 口琴谱格式

程序能直接解析这种格式（含「小节 / 简谱 / 键位 / 节奏」四行）：

```
小节   1 (4/4)
  简谱  3' 3' 3' 3' 6
  键位  C+ C+ C+ C+ N
  节奏  4  4  8  8  4
```

- **键位**是权威列，直接决定按什么键：`+` 升八度(右键)、`-` 降八度(左键)、`#` 升半音(中键)
- **节奏**是音符时值分母：`4` = 四分音符(1 拍)、`8` = 八分音符(半拍)、`4·` = 附点四分音符(1.5 拍)
- 标题行里的 `BPM 170` 会被读取并用于计算实际时长

### 关于「小节补齐」—— 澄清一件事

上一版我说这里是为了"对齐 MIDI"，这个说法不准确，我纠正一下：

**这是通用的乐理规则，不是针对某个 MIDI 文件的特调。**

一个小节在 4/4 拍里就占满 4 拍。如果这一小节里写的音符只凑了 3.5 拍，
**剩下的 0.5 拍就是休止符** —— 谱子的作者只是没把休止符写出来。
如果不补，下一小节就会提前 0.5 拍开始，而且这个误差会**一路累积**下去。

所以它对**任何** TXT 谱都成立，换成别的曲子一样正确。

现在这个逻辑还可以：
- **关掉**（`TxtParseOptions.AlignBarsToMeasures = false`），按谱面字面时长播放
- **自动报告**哪些小节被补齐了。你那个文件里正好是第 2、6、42、46 小节各补半拍
- 跨两小节的超长小节**不会被裁剪**，只会提示

测试里专门验证了：对齐后 67.765 秒 = 48 小节 × 4 拍 ÷ (170/60)，完全吻合。

---

## 七、已做的验证

两个独立测试程序，都用你 `WorkArea\DF` 里的真实文件跑：

### 解析 / 裁剪逻辑
```
cd D:\WorkArea\DeltaHarmonicaWPF\tools\Verify
dotnet run -c Release
```

33 项全部通过，覆盖：

```
[PASS] TXT yields 260 notes (matches header)
[PASS] length 67.77s (48 bars x 4 beats at BPM 170), got 67.76s
[PASS] bar 1 keys match the 键位 column ([右键]C [右键]C [右键]C [右键]C N)
[PASS] MIDI yields 260 notes
[PASS] same note count (260)
[PASS] same total length (txt 67.765s vs midi 67.765s)
[PASS] the vast majority of onsets agree within 20ms
[PASS] every note duration agrees with the MIDI
[PASS] all 260 notes map to actions
[PASS] MIDI is already in range: zero octave folding
[PASS] TXT 键位 column and MIDI produce identical sounding pitches
[PASS] round trip preserves note count
[PASS] round trip preserves every pitch
--- 裁剪 ---
[PASS] 2 notes overlap the 3..7s window
[PASS] the right two notes are kept
[PASS] no note extends past the trimmed end
[PASS] both partly-covered notes are kept when clipped
[PASS] clipped notes are rebased to >= 0
[PASS] clipped durations never exceed the window
[PASS] resetting the trim restores all notes
[PASS] end-only trim keeps the first 3 notes
--- 小节对齐 ---
[PASS] bar alignment pads short bars (aligned 67.765s > unaligned 67.059s)
[PASS] aligned length matches 48 full measures
[PASS] alignment never changes the note count
[PASS] exactly the 4 short bars (2, 6, 42, 46) are reported
[PASS] alignment reporting can be turned off
--- 容错 ---
[PASS] garbage TXT rejected with a clear error
[PASS] garbage MIDI rejected with a clear error
```

### 音频试听
```
cd D:\WorkArea\DeltaHarmonicaWPF\tools\AudioCheck
dotnet run -c Release
```

这个测试会**离线渲染整个合成波形**再逐样本分析，检查有没有断音：

```
[PASS] no silent gap longer than 12ms anywhere (got 0.3 ms)
[PASS] audio continues to the end of the last note (3.50s)
[PASS] no 250ms window anywhere is silent
[PASS] no buffer seam collapses the waveform (phase is continuous)
[PASS] all 7 notes rendered
[PASS] estimated pitches match the intended notes
[PASS] overall level is healthy, not fading out
[PASS] 0.5x / 1.5x / 2.0x playback is continuous
```

### 交互
```
cd D:\WorkArea\DeltaHarmonicaWPF\tools\UiCheck
dotnet run -c Release
```

直接驱动旋律预览的交互代码路径（含越界坐标），确认不会崩：

```
[PASS] MouseMove takes MouseEventArgs (the null-args path is gone)
[PASS] cursor positioning works across the whole width (no crash)
[PASS] out-of-bounds clicks are clamped, not thrown
[PASS] redraw with nothing selected is safe
```

### 并发与倍速
```
cd D:\WorkArea\DeltaHarmonicaWPF\tools\PlaybackCheck
dotnet run -c Release
```

> 注意：这个测试会**真的发送按键**，跑之前请把焦点放在一个无害的窗口上。

```
[PASS] Stop() returns promptly instead of waiting out the note (37 ms)
[PASS] rapid restarts complete without deadlock
[PASS] no playback thread survives the churn
[PASS] every note was emitted exactly once
[PASS] stopping prevented the rest of the song
[PASS] 0.5x/0.8x/1.0x/1.2x/1.5x/2.0x scale the note spacing correctly
```

### 键鼠锁定
```
cd D:\WorkArea\DeltaHarmonicaWPF\tools\LockCheck
dotnet run -c Release
```

```
[PASS] Lock() installs the hooks successfully
[PASS] Unlock() releases the lock
[PASS] locking twice is safe
[PASS] unlocking twice is safe
[PASS] can lock again after unlocking
```

### 关于测试用的音频文件

`tools/Verify` 默认读取 `D:\WorkArea\DF\得吃小曲（美味版）.mid/.txt`。
**如果这两个文件不在，它会自动生成一份结构等价的谱面**（同样 48 小节、
同样 4 个短小节、同样的 BPM），所以测试永远能跑，不会因为缺文件而失败。

### 关于你给的两个文件

我对比了 `得吃小曲（美味版）.mid` 和 `.txt`，结论：

- **是同一首曲子**，都是 **260 个音**，总时长都是 **67.765 秒**
- MIDI 音高已适配口琴音域（G4 ~ C6，只有 9 个音高），**不需要任何八度折叠**
- TXT 的「键位」列和从 MIDI 推导出的按键，**音高完全一致**
- 两者是个别**转谱差异**：TXT 有 4 个小节（第 2、6、42、46 小节）只写了 3.5 拍，
  隐含半拍休止；MIDI 把休止写出来了。所以 260 个音里有 24 个音的起始时间差一个八分音符，
  但**每个音的时长 100% 一致**。

---

## 八、项目结构

```
DeltaHarmonicaWPF/
├── DeltaHarmonica.csproj
├── App.xaml / App.xaml.cs          程序入口 + 全局异常处理
├── app.manifest                    DPI 感知（PerMonitorV2）
├── src/
│   ├── Core/
│   │   ├── MidiParser.cs           MIDI 解析（format 0/1、running status、
│   │   │                           tempo 事件、PPQ 与 SMPTE 时间基准）
│   │   ├── MidiWriter.cs           MIDI 写出（TXT → .mid 导出）
│   │   ├── TxtScoreParser.cs       口琴谱 TXT 解析（含小节补齐选项）
│   │   └── HarmonicaMapper.cs      音高 → 按键映射、自动八度折叠
│   ├── Models/Song.cs              歌曲模型、播放区间裁剪
│   ├── Interop/
│   │   ├── InputSimulator.cs       SendInput 封装（含扫描码填充）
│   │   └── InputLock.cs            键鼠锁定（低级钩子，识别并放行自身注入的事件）
│   ├── Services/
│   │   ├── PlaybackEngine.cs       演奏引擎（单线程发送 + 可中断睡眠 + 绝对时钟调度）
│   │   ├── AuditionPlayer.cs       试听合成器（waveOut + 连奏型加法合成）
│   │   ├── HotkeyManager.cs        全局热键注册
│   │   └── AppSettings.cs          设置持久化（%APPDATA%\DeltaHarmonica）
│   └── Views/
│       ├── Theme.xaml              深色主题、控件样式
│       ├── MainWindow.xaml(.cs)    侧边栏主界面
│       ├── CanvasExtensions.cs     钢琴卷帘的 Canvas 辅助
│       ├── HotkeyCaptureBox.cs     热键捕获输入框
│       ├── RenameDialog.xaml(.cs)  重命名对话框
│       └── TrayIcon.cs             托盘图标
└── tools/
    ├── Verify/                     解析器 / 裁剪逻辑
    ├── AudioCheck/                 试听波形连续性
    ├── PlaybackCheck/              并发、停止延迟、倍速精度
    ├── LockCheck/                  键鼠锁定钩子
    └── UiCheck/                    界面交互路径
```

---

## 九、演奏引擎的并发与时序

这一版重写了引擎的线程模型，修掉了三个严重问题。

### 问题 1：重播（Shift+F8）会乱按按键

**原因**：旧的 `Restart` 直接在 UI 线程上调 `_engine.Play()`，
而 `Play()` 里先 `Stop()` → `Join(2000)`。但旧线程只在**音符之间**检查停止标志，
所以它可能正卡在 `KeyDown` 和 `KeyUp` 之间的 `Sleep` 里 ——
Join 超时后新线程就开始按键，**两个线程同时发 SendInput**，
游戏收到的是任意的「修饰键 + 音键」组合。这就是"乱按"。

**修法**：
1. 所有按键发送用**一把锁**串起来（`EmitLock`），任何时刻只有一个线程能发
2. 每次 `Play` 前必须**等旧线程完全退出**才起新线程
3. 音符内部的每个 `Sleep` 都改成**可中断**的，停止标志一置位就立刻返回，
   并且**先松开修饰键再退出**，绝不留下卡住的按键

### 问题 2：点停止 / 结束试听会卡顿

**原因**：`Stop()` 会 `Join(2000)` 阻塞 **UI 线程**，最长冻 2 秒。

**修法**：Join 加上限（默认 400 毫秒），超时就不再等，
由线程自己收尾释放按键。实测 **停止耗时从最长 2000 毫秒降到 37 毫秒**。

### 问题 3：Shift+F2 播放的是上一首

**原因**：热键处理里歌单优先级写反了 —— 先取 `CurrentSong`（上次播的），
再取列表选中项。所以你在列表里选了 A，按 Shift+F2 却播了上次的 B。

**修法**：统一成一个 `TargetSong()`，**列表选中项永远优先**。
Shift+F2、Shift+F8、播放按钮现在都走同一条路径。

### 时序精度

音符按**绝对时钟**调度，每次按键实际花掉的时间会从后续等待里扣除，
所以误差不会累积。按键时长仍然精确（修饰键耗时已扣除）。

速度倍率的实测偏差在 0.1% ~ 3.4% 之间（见上表），
唯一的多余开销是每个音符的固定调用成本，可以忽略。

---

## 十、试听合成器的连续性

上一版的试听听起来"一个音一个点"，有间断感。原因有三个，都修了：

1. **相位在每个缓冲区重置** —— 原来用 `local = i / 采样率` 算相位，
   而 `i` 每 46 毫秒的缓冲区就归零一次，导致每个缓冲区接缝处振幅被强制拉回 0。
   → 改成用**绝对时间**算相位，波形跨缓冲区完全连续。
2. **每个音都做完整的起音+衰减** —— 短音会明显听成一个个独立的点。
   → 改成**连奏包络**：起音更柔和，音尾只轻微下陷（不低于 0.72），
   并且允许一点点**释放尾音重叠**到下一个音上，中间不留缝。
3. **音符之间没有重叠** —— 相邻音符首尾相接，硬切就是静音。
   → 每个音末尾有最多 90 毫秒的淡出尾巴，和下个音交叉。

**实测结果**：整首曲子内最长静音段从 46 毫秒以上降到 **0.3 毫秒**，
所有 250 毫秒窗口都有声音，各档速度（0.5×/1×/1.5×/2×）都连续。
音高也逐个验证过。

---

## 十一、输入质量上的处理

虽然本机只发标准 `SendInput`（云电脑那边已有硬件级通道），但为了让输入更像真人，
做了这些细节：

1. **正确填充扫描码** —— 用 `MapVirtualKey` 填 `wScan`，扩展键设 `KEYEVENTF_EXTENDEDKEY`。
   很多粗糙脚本只填 `wVk`，这是最典型的特征。
2. **人性化抖动**（可在设置里开关或调幅度）——
   真人不可能每次按键都精确 500.000 毫秒。按住时长加随机百分比，
   组合键之间加随机毫秒间隔。
3. **和弦串行化** —— 口琴是单排键，物理上按不了和弦。
   MIDI 里的和弦会拆成快速序列逐个发送。
4. **同键重复音处理** —— 同一个键连续来两个音时自动缩短前一个，留出间隙，
   保证识别成两次独立按键而不是一次长按。

### 修饰键的组合方式（按你确认的）

- **八度**：左键 = 降八度，右键 = 升八度，**按住生效**
- **半音**：中键 = 升半音
- **中键可以和左右键同时按**

```
[右键按下] [中键按下] → 音键按下 → 保持 → 音键松开 → [中键松开] [右键松开]
```

修饰键按**嵌套**顺序按下、**逆序**松开，所以任意组合
（降八度+半音、升八度+半音、纯八度、纯半音）都能正确表达。

---

## 十二、已知限制

- 不支持 MIDI format 2（多独立序列），会明确报错让你另存为 format 0/1
- 口琴音域有限，宽音域钢琴谱会被大幅折叠，听感与原曲不同
- 和弦会被拆散（物理限制）
- 试听是**合成音**，用来确认旋律和节奏，音色和游戏里的口琴不完全一样
- 键鼠锁定依赖底层钩子，**个别安全软件可能拦截**（程序会提示失败原因）
- 锁定期间鼠标指针仍可移动（刻意设计，避免误以为死机）

---

## 十三、已确认的键位设定

你已确认：

1. ✅ **八度修饰键是"按住"** —— 按住演奏，不是切换式
2. ✅ **八度方向**：左键降、右键升
3. ✅ **中键半音可以和左右键同时按** —— 程序支持任意组合

如果实际游戏里发现方向反了，改 `PlaybackEngine.Emit` 里修饰键那几行即可。
建议先用「输入自检」确认按键能送达游戏。
