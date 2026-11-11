using System.Collections.Concurrent;

namespace MidiPatchbay;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest.Run();

        using var mutex = new Mutex(true, @"Local\MidiPatchbay.SingleInstance", out bool first);
        ApplicationConfiguration.Initialize();
        if (!first)
        {
            MessageBox.Show("MIDI Patchbay is already running (check the notification area).", "MIDI Patchbay",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }

        var problem = PatchbayEngine.CheckPlatform();
        if (problem != null)
        {
            MessageBox.Show(problem + "\n\nVirtual ports are provided by Windows MIDI Services (Windows 11 25H2+).",
                "MIDI Patchbay", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        int ci = Array.IndexOf(args, "--config");
        var configPath = ci >= 0 && ci + 1 < args.Length ? args[ci + 1] : AppConfig.DefaultPath;
        var engine = new PatchbayEngine(configPath);
        try
        {
            Application.Run(new MainForm(engine, startMinimized: args.Contains("--minimized")));
        }
        finally
        {
            engine.Dispose();
        }
        return 0;
    }
}

/// <summary>
/// End-to-end test that behaves like two ordinary MIDI 1.0 apps:
///   1. "PB Test 1" routed to itself  : out(PB Test 1) -> in(PB Test 1)   (loopMIDI behaviour)
///   2. "PB Test 1" routed to "PB Test 2": out(PB Test 1) -> in(PB Test 2)
/// </summary>
static class SelfTest
{
    public static int Run()
    {
        void P(string s) { Console.WriteLine(s); }
        var problem = PatchbayEngine.CheckPlatform();
        if (problem != null) { P("FAIL platform: " + problem); return 2; }

        var cfgPath = Path.Combine(Path.GetTempPath(), "MidiPatchbay-selftest.json");
        File.Delete(cfgPath);
        using var engine = new PatchbayEngine(cfgPath);
        engine.Log += s => P("  log: " + s);
        engine.Start();

        var t1 = engine.AddVirtualPort("PB Test 1");            // loops to itself by default
        var t2 = engine.AddVirtualPort("PB Test 2", loopToItself: false);
        engine.SetRoute(t1.Key, t2.Key, true);

        // wait for WinMM to list the new ports
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 8000 &&
               !(WinMM.InputNames().Contains("PB Test 2") && WinMM.OutputNames().Contains("PB Test 1")))
            Thread.Sleep(200);
        P("WinMM inputs : " + string.Join(" | ", WinMM.InputNames()));
        P("WinMM outputs: " + string.Join(" | ", WinMM.OutputNames()));

        var got1 = new ConcurrentQueue<uint>();
        var got2 = new ConcurrentQueue<uint>();
        WinMM.MidiInProc cb1 = (h, msg, i, p1, p2) => { if (msg == WinMM.MIM_DATA) got1.Enqueue((uint)p1); };
        WinMM.MidiInProc cb2 = (h, msg, i, p1, p2) => { if (msg == WinMM.MIM_DATA) got2.Enqueue((uint)p1); };

        int failures = 0;
        IntPtr in1 = IntPtr.Zero, in2 = IntPtr.Zero, out1 = IntPtr.Zero;
        try
        {
            in1 = WinMM.OpenIn("PB Test 1", cb1);
            in2 = WinMM.OpenIn("PB Test 2", cb2);
            out1 = WinMM.OpenOut("PB Test 1");
            Thread.Sleep(300);

            var lat = System.Diagnostics.Stopwatch.StartNew();
            WinMM.midiOutShortMsg(out1, 0x00643C90); // Note On ch1, C4 (60), velocity 100
            WinMM.midiOutShortMsg(out1, 0x00074AB3); // CC ch4 #74 = 7
            while (lat.ElapsedMilliseconds < 2000 && (got1.Count < 2 || got2.Count < 2)) Thread.Sleep(1);
            P($"round trip wait: {lat.ElapsedMilliseconds} ms");

            string fmt(IEnumerable<uint> q) => string.Join(" ", q.Select(m => (m & 0xFFFFFF).ToString("X6")));
            P("received on 'PB Test 1' in: " + fmt(got1));
            P("received on 'PB Test 2' in: " + fmt(got2));
            var expect = new uint[] { 0x643C90, 0x074AB3 };
            bool ok1 = got1.Select(m => m & 0xFFFFFF).SequenceEqual(expect);
            bool ok2 = got2.Select(m => m & 0xFFFFFF).SequenceEqual(expect);
            P((ok1 ? "PASS" : "FAIL") + "  self loop (loopMIDI style)");
            P((ok2 ? "PASS" : "FAIL") + "  route PB Test 1 -> PB Test 2");
            if (!ok1) failures++;
            if (!ok2) failures++;

            // unrouting must stop delivery
            engine.SetRoute(t1.Key, t2.Key, false);
            while (got2.TryDequeue(out _)) { }
            WinMM.midiOutShortMsg(out1, 0x00403C80);
            Thread.Sleep(500);
            bool ok3 = got2.IsEmpty;
            P((ok3 ? "PASS" : "FAIL") + "  removing the route stops delivery");
            if (!ok3) failures++;
        }
        catch (Exception ex) { P("FAIL exception: " + ex.Message); failures++; }
        finally
        {
            if (out1 != IntPtr.Zero) WinMM.midiOutClose(out1);
            if (in1 != IntPtr.Zero) { WinMM.midiInStop(in1); WinMM.midiInClose(in1); }
            if (in2 != IntPtr.Zero) { WinMM.midiInStop(in2); WinMM.midiInClose(in2); }
            GC.KeepAlive(cb1); GC.KeepAlive(cb2);
        }

        engine.Dispose();
        Thread.Sleep(1500);
        bool gone = !WinMM.InputNames().Contains("PB Test 1");
        P((gone ? "PASS" : "FAIL") + "  ports removed when the patchbay exits");
        if (!gone) failures++;
        File.Delete(cfgPath);
        P(failures == 0 ? "ALL PASSED" : $"{failures} FAILURE(S)");
        return failures == 0 ? 0 : 1;
    }
}
