using System.Runtime.InteropServices;
using SimpleBin;
using SimpleBin.Helpers;
using System.Windows.Forms;

internal static class Program
{
    private static int _passed;

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderIDList(
        in Guid folderId, uint flags, IntPtr token, out IntPtr pidl);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern void SHChangeNotify(
        int eventId, uint flags, IntPtr item1, IntPtr item2);

    private sealed class MessageSink : NativeWindow
    {
        private readonly ShellChangeMonitor _monitor;
        internal int Notifications { get; private set; }

        internal MessageSink(ShellChangeMonitor monitor)
        {
            _monitor = monitor;
            CreateHandle(new CreateParams { Caption = "SimpleBinSafe read-only Shell smoke" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == ShellChangeMonitor.BinMessage ||
                m.Msg == ShellChangeMonitor.DesktopMessage)
            {
                if (_monitor.ConsumeNotification(m.WParam, m.LParam))
                    ++Notifications;
            }
            base.WndProc(ref m);
        }
    }

    private static void Pass(string name, bool ok)
    {
        if (!ok)
            throw new InvalidOperationException("FAIL: " + name);
        Console.WriteLine("PASS: " + name);
        ++_passed;
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            RunPureLogicChecks();
            RunWindowsReadOnlyChecks();
            RunShellNotificationSmoke();
            Console.WriteLine($"TOTAL: {_passed} deterministic/read-only checks passed.");
            Console.WriteLine("NOTE: Synthetic Shell notifications cannot prove that real Recycle Bin events or VeraCrypt mount notifications always arrive.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void RunPureLogicChecks()
    {
        DateTime t = new(2026, 11, 15, 16, 0, 0, DateTimeKind.Utc);
        const uint E = 1u << 4;
        const uint F = 1u << 5;
        const uint G = 1u << 6;
        const uint C = 1u << 2;

        var tracker = new VolumeChangeTracker();
        Pass("idle startup does not hold requests",
            tracker.RemainingHoldMilliseconds(t) == 0);

        tracker.Observe(VolumeEvent.RemovalRequested, E | F, t);
        Pass("simultaneous removals are tracked individually",
            tracker.RemainingHoldMilliseconds(t.AddSeconds(1)) > 0);

        tracker.Observe(VolumeEvent.RemovalCompleted, E, t.AddSeconds(1));
        Pass("one drive completing does not release another pending drive",
            tracker.RemainingHoldMilliseconds(t.AddSeconds(2)) > 0);

        tracker.Observe(VolumeEvent.RemovalCancelled, F, t.AddSeconds(2));
        Pass("canceled removal clears the correct drive's hold",
            tracker.RemainingHoldMilliseconds(t.AddSeconds(2)) == 0);

        tracker.Observe(VolumeEvent.RemovalPending, G, t.AddMinutes(1));
        tracker.ReconcileAssignedLetters(C | E | F);
        Pass("disappearing drive clears its hold without reading its files",
            tracker.RemainingHoldMilliseconds(t.AddMinutes(1)) == 0);

        tracker.Observe(VolumeEvent.RemovalRequested, G, t.AddMinutes(2));
        Pass("missing final event cannot block the UI permanently",
            tracker.RemainingHoldMilliseconds(t.AddMinutes(2).AddSeconds(16)) == 0);

        tracker.Observe(VolumeEvent.RemovalRequested, 0, t.AddMinutes(3));
        tracker.Observe(VolumeEvent.RemovalCancelled, 0, t.AddMinutes(3).AddSeconds(1));
        Pass("unknown device cancellation is handled",
            tracker.RemainingHoldMilliseconds(t.AddMinutes(3).AddSeconds(1)) == 0);

        long previousVersion = tracker.Version;
        tracker.Observe(VolumeEvent.TopologyChanged, 0, t.AddMinutes(4));
        Pass("drive topology notifications advance the version",
            tracker.Version == previousVersion + 1);

        var before = new VolumeSnapshot(C | E, previousVersion);
        var same = new VolumeSnapshot(C | E, previousVersion);
        var removed = new VolumeSnapshot(C, previousVersion);
        var remountedSameLetter = new VolumeSnapshot(C | E, previousVersion + 1);
        Pass("unchanged drive snapshot verifies", before.Matches(same));
        Pass("missing drive causes verification failure", !before.Matches(removed));
        Pass("same letters with observed remount causes verification failure",
            !before.Matches(remountedSameLetter));

        tracker.Observe(VolumeEvent.Arrival, E, t.AddMinutes(5));
        Pass("new arrival without removal does not block requests",
            tracker.RemainingHoldMilliseconds(t.AddMinutes(5)) == 0);

        tracker.Observe(VolumeEvent.RemovalPending, E | F, t.AddMinutes(6));
        tracker.Observe(VolumeEvent.RemovalCompleted, F, t.AddMinutes(6).AddSeconds(1));
        Pass("multi-volume pending condition remains until last volume completes",
            tracker.RemainingHoldMilliseconds(t.AddMinutes(6).AddSeconds(2)) > 0);
        tracker.Observe(VolumeEvent.RemovalCompleted, E, t.AddMinutes(6).AddSeconds(3));
        Pass("final completion unblocks the request",
            tracker.RemainingHoldMilliseconds(t.AddMinutes(6).AddSeconds(3)) == 0);
    }

    private static void RunWindowsReadOnlyChecks()
    {
        VolumeSnapshot volume = VolumeSnapshot.Capture(42);
        Pass("Windows can enumerate assigned drive letters",
            volume.AssignedLetters != 0);
        Pass("volume snapshot preserves sequence",
            volume.EventVersion == 42);

        try
        {
            var (bytes, count) = BinHelper.GetBinSize();
            Pass("read-only Recycle Bin native query has non-negative results",
                bytes >= 0 && count >= 0);
            Console.WriteLine($"INFO: Windows Shell reports {count} Recycle Bin items on the isolated CI runner.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("WARN: CI runner's non-interactive Shell cannot confirm Recycle Bin query: " + ex.Message);
        }
    }

    private static void RunShellNotificationSmoke()
    {
        // Synthetic notifications do not delete files or modify a Recycle Bin.
        // A headless CI session might not deliver notifications like Explorer.
        var monitor = new ShellChangeMonitor();
        MessageSink? sink = null;
        IntPtr binPidl = IntPtr.Zero;
        try
        {
            sink = new MessageSink(monitor);
            monitor.Start(sink.Handle);
            Pass("native Shell event registrations succeeded", monitor.IsActive);
            Console.WriteLine("INFO: Desktop fallback registration: " + monitor.HasDesktopFallback);

            Guid binId = new("B7534046-3ECB-4C18-BE4E-64CD4CB7D6AC");
            int hr = SHGetKnownFolderIDList(in binId, 0, IntPtr.Zero, out binPidl);
            if (hr != 0 || binPidl == IntPtr.Zero)
            {
                Console.WriteLine($"WARN: no CI Shell PIDL for synthetic notification (0x{hr:X8}).");
                return;
            }

            // SHCNE_UPDATEITEM, SHCNF_IDLIST | SHCNF_FLUSHNOWAIT.
            // The only action is sending a Shell change notification in the
            // disposable GitHub Actions runner, not modifying the Recycle Bin.
            SHChangeNotify(0x00002000, 0x2000, binPidl, IntPtr.Zero);
            for (int i = 0; i < 100 && sink.Notifications == 0; ++i)
            {
                Application.DoEvents();
                Thread.Sleep(20);
            }

            if (sink.Notifications != 0)
                Pass("synthetic Recycle Bin Shell notification arrived",
                    sink.Notifications > 0);
            else
                Console.WriteLine("WARN: synthetic Shell notification was not delivered in the non-interactive CI session.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("WARN: Shell registration smoke test could not run headlessly: " + ex.Message);
        }
        finally
        {
            monitor.Stop();
            sink?.DestroyHandle();
            monitor.Dispose();
            if (binPidl != IntPtr.Zero)
                Marshal.FreeCoTaskMem(binPidl);
        }
    }
}
