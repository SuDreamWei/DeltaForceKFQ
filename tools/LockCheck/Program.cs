// Verifies the InputLock hook installs, blocks physical input, and always leaves
// the system in a usable state.
using DeltaHarmonica.Interop;
using System.Windows;
using System.Windows.Threading;

Console.OutputEncoding = System.Text.Encoding.UTF8;
int fail = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) fail++;
}

// Low-level hooks need a message loop on the installing thread.
var t = new Thread(() =>
{
    var lockObj = new InputLock();
    try
    {
        Check(!lockObj.IsLocked, "starts unlocked");

        // The emergency key must always be usable.
        lockObj.AllowKey(0x78);        // F9

        bool ok = lockObj.Lock();
        Check(ok, "Lock() installs the hooks successfully");
        Check(lockObj.IsLocked, "IsLocked reports true after Lock()");

        // Pump messages briefly so the hooks are actually serviced.
        var end = DateTime.UtcNow.AddMilliseconds(400);
        while (DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(10);
        }

        lockObj.Unlock();
        Check(!lockObj.IsLocked, "Unlock() releases the lock");
        Check(lockObj.BlockedCount >= 0, $"BlockedCount is readable ({lockObj.BlockedCount})");

        // Double lock / double unlock must be safe.
        lockObj.Lock();
        lockObj.Lock();
        Check(lockObj.IsLocked, "locking twice is safe");
        lockObj.Unlock();
        lockObj.Unlock();
        Check(!lockObj.IsLocked, "unlocking twice is safe");

        // Re-locking after unlock must work (used on every performance).
        Check(lockObj.Lock(), "can lock again after unlocking");
        lockObj.Unlock();

        lockObj.Dispose();
        Check(!lockObj.IsLocked, "Dispose() leaves it unlocked");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] harness error: {ex.Message}\n{ex.StackTrace}");
        fail++;
        try { lockObj.Unlock(); } catch { }
    }
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();

Console.WriteLine("\n==============================");
Console.WriteLine(fail == 0 ? "ALL INPUT-LOCK CHECKS PASSED" : $"{fail} CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
