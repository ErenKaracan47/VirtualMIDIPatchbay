using System.Runtime.InteropServices;

namespace MidiPatchbay;

/// <summary>Classic MIDI 1.0 (WinMM) API, used by the self-test to act like an ordinary app.</summary>
static class WinMM
{
    public const int MIM_DATA = 0x3C3;
    const int CALLBACK_FUNCTION = 0x30000;

    public delegate void MidiInProc(IntPtr h, int msg, IntPtr inst, IntPtr p1, IntPtr p2);

    [DllImport("winmm.dll")] static extern uint midiInGetNumDevs();
    [DllImport("winmm.dll")] static extern uint midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int midiInGetDevCaps(UIntPtr id, ref MIDIINCAPS c, uint sz);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int midiOutGetDevCaps(UIntPtr id, ref MIDIOUTCAPS c, uint sz);
    [DllImport("winmm.dll")] public static extern int midiInOpen(out IntPtr h, uint id, MidiInProc cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] public static extern int midiInStart(IntPtr h);
    [DllImport("winmm.dll")] public static extern int midiInStop(IntPtr h);
    [DllImport("winmm.dll")] public static extern int midiInClose(IntPtr h);
    [DllImport("winmm.dll")] public static extern int midiOutOpen(out IntPtr h, uint id, IntPtr cb, IntPtr inst, int flags);
    [DllImport("winmm.dll")] public static extern int midiOutShortMsg(IntPtr h, uint msg);
    [DllImport("winmm.dll")] public static extern int midiOutClose(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MIDIINCAPS { public ushort wMid, wPid; public uint ver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public uint sup; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MIDIOUTCAPS { public ushort wMid, wPid; public uint ver; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname; public ushort tech, voices, notes, mask; public uint sup; }

    public static List<string> InputNames()
    {
        var l = new List<string>();
        uint n = midiInGetNumDevs();
        for (uint i = 0; i < n; i++) { var c = new MIDIINCAPS(); midiInGetDevCaps((UIntPtr)i, ref c, (uint)Marshal.SizeOf<MIDIINCAPS>()); l.Add(c.szPname); }
        return l;
    }

    public static List<string> OutputNames()
    {
        var l = new List<string>();
        uint n = midiOutGetNumDevs();
        for (uint i = 0; i < n; i++) { var c = new MIDIOUTCAPS(); midiOutGetDevCaps((UIntPtr)i, ref c, (uint)Marshal.SizeOf<MIDIOUTCAPS>()); l.Add(c.szPname); }
        return l;
    }

    public static IntPtr OpenIn(string name, MidiInProc cb)
    {
        int idx = InputNames().IndexOf(name);
        if (idx < 0) throw new Exception($"MIDI input '{name}' not found");
        int r = midiInOpen(out var h, (uint)idx, cb, IntPtr.Zero, CALLBACK_FUNCTION);
        if (r != 0) throw new Exception($"midiInOpen('{name}') failed: {r}");
        midiInStart(h);
        return h;
    }

    public static IntPtr OpenOut(string name)
    {
        int idx = OutputNames().IndexOf(name);
        if (idx < 0) throw new Exception($"MIDI output '{name}' not found");
        int r = midiOutOpen(out var h, (uint)idx, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new Exception($"midiOutOpen('{name}') failed: {r}");
        return h;
    }
}
