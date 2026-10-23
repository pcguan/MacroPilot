using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using MacroPilot.Input;
using MacroPilot.Models;
using MacroPilot.Services;

// Explicit, opt-in desktop integration probe. Never part of the app payload or unit test run.
internal static class Program
{
    // Untagged injection represents a separate input source, including a move overwritten by our next frame.
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
    private static void InjectExternalMove(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = GetSystemMetrics(78), height = GetSystemMetrics(79);
        mouse_event(0xC001, (uint)Math.Round((x - left) * 65535.0 / Math.Max(1, width - 1)),
            (uint)Math.Round((y - top) * 65535.0 / Math.Max(1, height - 1)), 0, 0);
    }
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfo(uint action, uint param, int[] value, uint flags);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private sealed record Sample(double Ms, int X, int Y);
    private sealed record Result(string Backend, bool Human, string From, string To, string Reason,
        double Ms, int ErrorX, int ErrorY, bool Expected, bool ButtonReleased, double ReleaseLagMs, bool ObservedHeld, bool PauseStable, string[] Logs, Sample[] Samples);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 3 || args[0] != "--execute") return 2;
        SetProcessDpiAwarenessContext(-4);
        string mode = args[1], output = Path.GetFullPath(args[2]);
        if (Process.GetCurrentProcess().SessionId == 0) throw new InvalidOperationException("Interactive desktop required.");
        bool interfere = args.Contains("--interfere"), stop = args.Contains("--stop"), pause = args.Contains("--pause");
        bool control = interfere || stop || pause, drag = args.Contains("--drag");
        int repeats = int.TryParse(args.FirstOrDefault(a => a.StartsWith("--repeat="))?.Split('=')[1], out int count) ? Math.Clamp(count, 1, 20) : 1;
        var screens = ScreenInfo.All();
        using var desktop = drag ? new ProbeDesktop(screens) : null;
        var original = ScreenInfo.CursorPos();
        var accel = new int[3]; var speed = new int[1];
        if (!SystemParametersInfo(3, 0, accel, 0) || !SystemParametersInfo(112, 0, speed, 0)) return 3;
        var results = new List<Result>();
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var total = Stopwatch.StartNew();
        using IInputBackend backend = mode == "native" ? new NativeInputDevice("ScanCode") :
            new Ch9329Device(args[3], int.Parse(args[4]));
        bool restored = false, completed = false;
        string? failure = null;
        void Save() => File.WriteAllText(output, JsonSerializer.Serialize(new {
            Version = typeof(MacroRunner).Assembly.GetName().Version?.ToString(), Mode = mode,
            Screens = screens, Original = new { original.vx, original.vy }, Accel = accel, Speed = speed[0],
            Restored = restored, Completed = completed, Failure = failure,
            Interference = interfere, Stop = stop, Pause = pause, Drag = drag, Results = results }, Json));
        try
        {
            if (!backend.Open()) throw new IOException("Cannot open input backend.");
            for (int repetition = 0; repetition < repeats; repetition++)
            foreach (bool human in control ? new[] { true } : new[] { false, true })
            foreach (var from in screens)
            foreach (var to in screens)
            {
                if (control && from.Device != to.Device) continue;
                if (total.Elapsed.TotalSeconds > 180 || GetAsyncKeyState(0x7A) < 0) return 4;
                // Setup is explicitly software positioning, outside the measured operation.
                NativeInputDevice.MoveVirtual(from.Left + from.Width / 3, from.Top + from.Height / 3);
                Thread.Sleep(150);
                int tx = to.Left + to.Width * 2 / 3, ty = to.Top + to.Height * 2 / 3;
                var step = new MacroStep { Type = "MouseMove", MoveMonitor = to.Device,
                    MoveNormX = (tx - to.Left) / (double)to.Width,
                    MoveNormY = (ty - to.Top) / (double)to.Height, Humanize = human };
                if (drag)
                {
                    if (!desktop!.OwnsPoint(from.Left + from.Width / 3, from.Top + from.Height / 3) || !desktop.OwnsPoint(tx, ty))
                        throw new InvalidOperationException("Drag surface is not topmost; refusing real button input.");
                    step.Type = "MouseDrag";
                    step.MoveMonitor = from.Device; step.MoveNormX = 1.0 / 3; step.MoveNormY = 1.0 / 3;
                    step.DragEndMonitor = to.Device;
                    step.DragEndNormX = (tx - to.Left) / (double)to.Width;
                    step.DragEndNormY = (ty - to.Top) / (double)to.Height;
                }
                var plan = new MacroPlan { LoopCount = 1, PauseOnFail = false };
                plan.Steps.Add(step);
                var runner = new MacroRunner(backend);
                var logs = new List<string>(); string reason = "MissingFinished";
                runner.Log += (level, message) => { lock (logs) logs.Add(level + ":" + message); };
                runner.Finished += r => reason = r;
                var samples = new List<Sample>(); var watch = Stopwatch.StartNew();
                runner.Start(plan, 0);
                bool stopped = false, acted = false, resumed = false, pauseStable = true, observedHeld = false;
                double actedAt = 0;
                (int vx, int vy)? pausedPosition = null;
                while (!runner.Completion.IsCompleted)
                {
                    ProbeDesktop.Pump();
                    var p = ScreenInfo.CursorPos();
                    observedHeld |= GetAsyncKeyState(1) < 0;
                    if (samples.Count < 10000) samples.Add(new(watch.Elapsed.TotalMilliseconds, p.vx, p.vy));
                    if (!acted && control && watch.Elapsed.TotalMilliseconds >= 80 && (!drag || observedHeld))
                    {
                        acted = true; actedAt = watch.Elapsed.TotalMilliseconds;
                        if (interfere) InjectExternalMove(from.Left + from.Width / 6, from.Top + from.Height / 6);
                        else if (stop) runner.Stop();
                        else runner.Pause();
                    }
                    if (pause && acted && !resumed)
                    {
                        if (watch.Elapsed.TotalMilliseconds >= actedAt + 50)
                        {
                            pausedPosition ??= p;
                            if (pausedPosition.Value != p) pauseStable = false;
                        }
                        if (watch.Elapsed.TotalMilliseconds >= actedAt + 200) { runner.Resume(); resumed = true; }
                    }
                    if (watch.Elapsed.TotalSeconds > 12 || GetAsyncKeyState(0x7A) < 0)
                    { runner.Stop(); stopped = true; }
                    if (watch.Elapsed.TotalSeconds > 16) throw new TimeoutException("Runner ignored stop.");
                    Thread.Sleep(2);
                }
                runner.Completion.GetAwaiter().GetResult();
                var end = ScreenInfo.CursorPos();
                double executedMs = watch.Elapsed.TotalMilliseconds;
                var release = Stopwatch.StartNew();
                // A serial write completes before its USB report reaches the OS. Measure that lag separately.
                while (GetAsyncKeyState(1) < 0 && release.ElapsedMilliseconds < 200)
                { ProbeDesktop.Pump(); Thread.Sleep(1); }
                bool expected = interfere || stop ? acted && reason == "Stopped" :
                    reason == "Done" && Math.Abs(end.vx - tx) <= 6 && Math.Abs(end.vy - ty) <= 6;
                results.Add(new(mode, human, from.Device, to.Device, reason, executedMs,
                    end.vx - tx, end.vy - ty, expected && (!pause || resumed && pauseStable) && (!drag || observedHeld), GetAsyncKeyState(1) >= 0,
                    release.Elapsed.TotalMilliseconds, observedHeld, pauseStable, logs.ToArray(), samples.ToArray()));
                Save();
                if (stopped) return 4;
            }
            completed = true;
            return results.Count > 0 && results.All(r => r.Expected && r.ButtonReleased) ? 0 : 1;
        }
        catch (Exception ex) { failure = ex.ToString(); return 1; }
        finally
        {
            try { backend.ReleaseAll(); } catch (Exception ex) { failure ??= ex.ToString(); }
            try { backend.Close(); } catch (Exception ex) { failure ??= ex.ToString(); }
            var afterAccel = new int[3]; var afterSpeed = new int[1];
            restored = SystemParametersInfo(3, 0, afterAccel, 0) && SystemParametersInfo(112, 0, afterSpeed, 0)
                && accel.SequenceEqual(afterAccel) && speed.SequenceEqual(afterSpeed);
            NativeInputDevice.MoveVirtual(original.vx, original.vy);
            Save();
        }
    }
}
