using System.IO;
using DeltaHarmonica.Core;
var txt = TxtScoreParser.Parse(@"D:\WorkArea\DF\得吃小曲（美味版）.txt");
var midi = MidiParser.Parse(@"D:\WorkArea\DF\得吃小曲（美味版）.mid");
for (int i = 0; i < 8; i++) {
  var t = txt.Actions[i]; var m = midi.Notes[i];
  Console.WriteLine($"  i={i}  txt t={t.TimeSec:F3} d={t.DurationSec:F3} {t.ComboText,-8} | midi t={m.TimeSec:F3} d={m.DurationSec:F3}");
}
Console.WriteLine();
Console.WriteLine($"txt total dur = {txt.Actions.Sum(a=>a.DurationSec):F2}  end = {txt.LengthSec:F2}");
Console.WriteLine($"midi total  = {midi.Notes.Sum(n=>n.DurationSec):F2}  end = {midi.LengthSec:F2}");
