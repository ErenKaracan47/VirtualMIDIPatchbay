using System.Collections.Concurrent;
using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Enumeration;
using Windows.Devices.Midi2.Transports.Loopback;

namespace MidiPatchbay;

/// <summary>
/// A routable node: either a virtual port this app created, or an existing MIDI endpoint
/// (hardware, another app's port, the GS synth...).
/// </summary>
public sealed class Node
{
    public string Key;            // "v:<portId>" or "e:<endpointDeviceId>"
    public string Name;
    public bool IsVirtual;
    public string PortId;         // virtual only
    public Guid AssociationId;    // virtual only: loopback pair id
    public string ConnectEndpointId; // endpoint we open (B side for virtual ports)
    public volatile bool Online;

    internal MidiEndpointConnection Conn;
    internal volatile Node[] Targets = Array.Empty<Node>();

    public long InCount, OutCount;
    public long LastInTick, LastOutTick;

    public bool IsOpen => Conn != null;
    public override string ToString() => Name;
}

/// <summary>
/// The patchbay. Each virtual port is a Windows MIDI Services loopback pair:
///   side A  "Name"            -> visible to every app (MIDI 1.0 in + out, and MIDI 2.0)
///   side B  "Name (patchbay)" -> UMP-only, hidden from MIDI 1.0 apps, opened by us.
/// What apps send to "Name" arrives on B; what we send to B comes out of "Name" for apps.
/// Routing "Name" to itself therefore gives loopMIDI behaviour.
/// </summary>
public sealed class PatchbayEngine : IDisposable
{
    const string PortIdPrefix = "MPB";

    readonly object _gate = new();
    readonly string _configPath;
    readonly Dictionary<string, Node> _nodes = new();
    readonly HashSet<(string From, string To)> _routes = new();
    MidiSession _session;
    MidiEndpointDeviceWatcher _watcher;
    System.Threading.Timer _refreshDebounce;
    bool _disposed;

    public AppConfig Config { get; }
    public event Action Changed;
    public event Action<string> Log;

    public volatile bool MonitorEnabled;
    public readonly ConcurrentQueue<string> MonitorQueue = new();

    public PatchbayEngine(string configPath)
    {
        _configPath = configPath;
        Config = AppConfig.Load(configPath);
        foreach (var r in Config.Routes) _routes.Add((r.From, r.To));
    }

    // ---------------------------------------------------------------- startup

    public static string CheckPlatform()
    {
        try
        {
            if (!MidiApi.EnsureServiceAvailable())
                return "The Windows MIDI Services service (midisrv) is not available.";
            if (!MidiLoopbackManager.IsTransportAvailable)
                return "The Windows MIDI Services loopback transport is not available.";
            var mode = MidiApi.GetCurrentlySelectedApiMode();
            if (mode == MidiApiMode.LegacyMode)
                return "Windows MIDI is in legacy mode, so MIDI 1.0 apps would not see the virtual ports.";
            return null;
        }
        catch (Exception ex)
        {
            return "Could not load Windows MIDI Services: " + ex.Message +
                   "\nThis app needs Windows 11 25H2 or later.";
        }
    }

    public void Start()
    {
        _session = MidiSession.Create("MIDI Patchbay");
        RemoveOrphanedLoopbacks();

        foreach (var p in Config.Ports.ToList())
        {
            try { CreateVirtualNode(p); }
            catch (Exception ex) { Emit($"Could not create port '{p.Name}': {ex.Message}"); }
        }

        RefreshEndpoints();

        _refreshDebounce = new System.Threading.Timer(_ => { try { RefreshEndpoints(); } catch (Exception ex) { Emit("Refresh failed: " + ex.Message); } });
        _watcher = MidiEndpointDeviceWatcher.Create(MidiEndpointDeviceInformationFilters.AllStandardEndpoints);
        _watcher.Added += (_, _) => ScheduleRefresh();
        _watcher.Removed += (_, _) => ScheduleRefresh();
        _watcher.Updated += (_, _) => ScheduleRefresh();
        _watcher.Start();
    }

    void ScheduleRefresh() { if (!_disposed) _refreshDebounce?.Change(400, Timeout.Infinite); }

    static bool IsOurEndpoint(string endpointDeviceId)
    {
        var id = endpointDeviceId.ToLowerInvariant();
        var p = PortIdPrefix.ToLowerInvariant();
        return id.Contains("midiu_loop_a_" + p) || id.Contains("midiu_loop_b_" + p);
    }

    /// <summary>Loopbacks survive process exit; remove any we left behind (crash etc.).</summary>
    void RemoveOrphanedLoopbacks()
    {
        foreach (var e in MidiLoopbackManager.GetActiveLoopbackEntries())
        {
            if (e.EndpointA != null && IsOurEndpoint(e.EndpointA.EndpointDeviceId))
            {
                MidiLoopbackManager.RemoveTransientLoopback(new MidiLoopbackRemovalConfig(e.AssociationId));
            }
        }
    }

    // ---------------------------------------------------------------- virtual ports

    public IReadOnlyList<Node> Nodes
    {
        get
        {
            lock (_gate)
                return _nodes.Values
                    .OrderBy(n => n.IsVirtual ? 0 : 1)
                    .ThenBy(n => n.IsVirtual ? Config.Ports.FindIndex(p => "v:" + p.Id == n.Key) : 0)
                    .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
        }
    }

    public bool HasRoute(string from, string to) { lock (_gate) return _routes.Contains((from, to)); }

    public static string ValidatePortName(string name, IEnumerable<string> existing)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return "Enter a port name.";
        if (name.Length > 31) return "Port names can be at most 31 characters (a MIDI 1.0 limit).";
        if (existing.Any(e => string.Equals(e, name, StringComparison.CurrentCultureIgnoreCase)))
            return $"A port named '{name}' already exists.";
        return null;
    }

    public Node AddVirtualPort(string name, bool loopToItself = true)
    {
        name = name.Trim();
        lock (_gate)
        {
            var err = ValidatePortName(name, Config.Ports.Select(p => p.Name));
            if (err != null) throw new InvalidOperationException(err);
        }
        var pc = new PortConfig { Id = PortIdPrefix + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(), Name = name };
        var node = CreateVirtualNode(pc);
        lock (_gate)
        {
            Config.Ports.Add(pc);
            if (loopToItself) _routes.Add((node.Key, node.Key));
            RebuildTargetsLocked();
            SaveLocked();
        }
        Changed?.Invoke();
        return node;
    }

    Node CreateVirtualNode(PortConfig pc)
    {
        var a = new MidiLoopbackEndpointDefinition(pc.Name, "Virtual MIDI port (MIDI Patchbay)", pc.Id);
        var b = new MidiLoopbackEndpointDefinition(pc.Name + " (patchbay)", "Internal side of a MIDI Patchbay port", pc.Id)
        {
            CreateOnlyUmpEndpoint = true // keep it out of MIDI 1.0 port lists
        };

        MidiLoopbackCreationResponse resp = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var cfg = new MidiLoopbackCreationConfig(a, b) { FeedbackProtection = MidiLoopbackFeedbackProtection.Mute };
            resp = MidiLoopbackManager.CreateTransientLoopback(cfg);
            if (resp.Success) break;
            Thread.Sleep(400); // the service can need a moment right after it starts
        }
        if (resp == null || !resp.Success)
            throw new InvalidOperationException($"service refused to create the port ({resp?.ErrorCode} {resp?.ErrorMessage})");

        var entry = resp.CreatedLoopbackEntry;
        var node = new Node
        {
            Key = "v:" + pc.Id,
            Name = pc.Name,
            IsVirtual = true,
            PortId = pc.Id,
            AssociationId = entry.AssociationId,
            ConnectEndpointId = entry.EndpointB.EndpointDeviceId,
            Online = true,
        };
        lock (_gate)
        {
            _nodes[node.Key] = node;
            OpenLocked(node, retries: 15);
        }
        Emit($"Created virtual port '{pc.Name}'");
        return node;
    }

    public void RemoveVirtualPort(Node node)
    {
        if (!node.IsVirtual) return;
        lock (_gate)
        {
            CloseLocked(node);
            _nodes.Remove(node.Key);
            Config.Ports.RemoveAll(p => "v:" + p.Id == node.Key);
            _routes.RemoveWhere(r => r.From == node.Key || r.To == node.Key);
            RebuildTargetsLocked();
            SaveLocked();
        }
        try { MidiLoopbackManager.RemoveTransientLoopback(new MidiLoopbackRemovalConfig(node.AssociationId)); }
        catch (Exception ex) { Emit("Remove failed: " + ex.Message); }
        Emit($"Removed virtual port '{node.Name}'");
        Changed?.Invoke();
    }

    public void MoveVirtualPort(Node node, int delta)
    {
        lock (_gate)
        {
            int i = Config.Ports.FindIndex(p => "v:" + p.Id == node.Key);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= Config.Ports.Count) return;
            (Config.Ports[i], Config.Ports[j]) = (Config.Ports[j], Config.Ports[i]);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- routing

    public void SetRoute(string from, string to, bool on)
    {
        lock (_gate)
        {
            bool changed = on ? _routes.Add((from, to)) : _routes.Remove((from, to));
            if (!changed) return;
            RebuildTargetsLocked();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public void ClearRoutes()
    {
        lock (_gate) { _routes.Clear(); RebuildTargetsLocked(); SaveLocked(); }
        Changed?.Invoke();
    }

    public void RefreshEndpoints()
    {
        if (_disposed) return;
        var found = MidiEndpointDeviceInformation.FindAll(
            MidiEndpointDeviceInformationSortOrder.Name,
            MidiEndpointDeviceInformationFilters.AllStandardEndpoints);

        lock (_gate)
        {
            if (_disposed) return;
            var seen = new HashSet<string>();
            foreach (var e in found)
            {
                var id = e.EndpointDeviceId;
                if (IsOurEndpoint(id)) continue;
                if (e.EndpointPurpose != MidiEndpointDevicePurpose.NormalMessageEndpoint &&
                    e.EndpointPurpose != MidiEndpointDevicePurpose.InBoxGeneralMidiSynth) continue;

                var key = "e:" + id;
                seen.Add(key);
                if (!_nodes.TryGetValue(key, out var n))
                {
                    n = new Node { Key = key, ConnectEndpointId = id };
                    _nodes[key] = n;
                }
                n.Name = e.Name;
                n.Online = true;
                Config.KnownNames[key] = e.Name;
            }

            // devices that went away stay listed (offline) if they have routes, so replugging restores them
            foreach (var n in _nodes.Values.Where(n => !n.IsVirtual).ToList())
            {
                if (seen.Contains(n.Key)) continue;
                n.Online = false;
                CloseLocked(n);
                if (!_routes.Any(r => r.From == n.Key || r.To == n.Key)) _nodes.Remove(n.Key);
            }
            foreach (var key in _routes.SelectMany(r => new[] { r.From, r.To }).Distinct())
            {
                if (_nodes.ContainsKey(key) || !key.StartsWith("e:")) continue;
                _nodes[key] = new Node
                {
                    Key = key,
                    ConnectEndpointId = key[2..],
                    Name = Config.KnownNames.TryGetValue(key, out var nm) ? nm : key[2..],
                    Online = false,
                };
            }
            // routes that point at virtual ports which no longer exist are dropped
            _routes.RemoveWhere(r => (r.From.StartsWith("v:") && !_nodes.ContainsKey(r.From)) ||
                                     (r.To.StartsWith("v:") && !_nodes.ContainsKey(r.To)));

            RebuildTargetsLocked();
            SaveLocked();
        }
        Changed?.Invoke();
    }

    /// <summary>Open the endpoints that take part in routing, close the rest, and publish target arrays.</summary>
    void RebuildTargetsLocked()
    {
        foreach (var n in _nodes.Values)
        {
            bool needed = n.IsVirtual || _routes.Any(r => r.From == n.Key || r.To == n.Key);
            if (needed && n.Online && n.Conn == null) OpenLocked(n, retries: 2);
            else if (!needed && n.Conn != null) CloseLocked(n);
        }
        foreach (var n in _nodes.Values)
        {
            n.Targets = _routes.Where(r => r.From == n.Key)
                .Select(r => _nodes.TryGetValue(r.To, out var t) ? t : null)
                .Where(t => t != null && t.Online && t.Conn != null)
                .ToArray();
        }
    }

    void OpenLocked(Node n, int retries)
    {
        for (int i = 0; i <= retries; i++)
        {
            MidiEndpointConnection conn = null;
            try
            {
                conn = _session.CreateEndpointConnection(n.ConnectEndpointId, new MidiEndpointConnectionSettings(false, true));
                if (conn != null)
                {
                    var node = n;
                    conn.MessageReceived += (_, args) => OnMessage(node, args);
                    if (conn.Open()) { n.Conn = conn; return; }
                    _session.DisconnectEndpointConnection(conn.ConnectionId);
                }
            }
            catch (Exception ex)
            {
                if (i == retries) Emit($"Could not open '{n.Name}': {ex.Message}");
                if (conn != null) try { _session.DisconnectEndpointConnection(conn.ConnectionId); } catch { }
            }
            if (i < retries) Thread.Sleep(200);
        }
        Emit($"Could not open '{n.Name}'");
    }

    void CloseLocked(Node n)
    {
        var c = n.Conn;
        if (c == null) return;
        n.Conn = null;
        try { _session.DisconnectEndpointConnection(c.ConnectionId); } catch { }
    }

    // ---------------------------------------------------------------- message path (SDK callback thread)

    void OnMessage(Node src, MidiMessageReceivedEventArgs args)
    {
        uint w0, w1, w2, w3;
        byte count = args.FillWords(out w0, out w1, out w2, out w3);
        long now = Environment.TickCount64;
        Interlocked.Increment(ref src.InCount);
        src.LastInTick = now;

        uint mt = w0 >> 28;
        // UMP utility (JR timestamps, NOOP) and stream messages (endpoint discovery) are
        // conversations between one endpoint and the service; never forward them.
        if (mt == 0x0 || mt == 0xF) return;

        var targets = src.Targets;
        foreach (var d in targets)
        {
            var c = d.Conn;
            if (c == null) continue;
            uint x0 = w0;
            // Virtual ports expose one MIDI 1.0 port on group 1, so fold every group onto it.
            if (d.IsVirtual && mt >= 0x1 && mt <= 0x5) x0 &= 0xF0FFFFFF;
            try
            {
                switch (count)
                {
                    case 1: c.SendSingleMessageWords(0, x0); break;
                    case 2: c.SendSingleMessageWords(0, x0, w1); break;
                    case 3: c.SendSingleMessageWords(0, x0, w1, w2); break;
                    default: c.SendSingleMessageWords(0, x0, w1, w2, w3); break;
                }
                Interlocked.Increment(ref d.OutCount);
                d.LastOutTick = now;
            }
            catch { /* destination vanished mid-send */ }
        }

        if (MonitorEnabled && MonitorQueue.Count < 5000)
        {
            if (Config.HideClockInMonitor && mt == 0x1)
            {
                uint st = (w0 >> 16) & 0xFF;
                if (st == 0xF8 || st == 0xFE) return;
            }
            string dests = targets.Length == 0 ? "(no route)" : string.Join(", ", targets.Select(t => t.Name));
            MonitorQueue.Enqueue($"{DateTime.Now:HH:mm:ss.fff}  {src.Name} → {dests}   {UmpFormat.Describe(count, w0, w1, w2, w3)}");
        }
    }

    // ---------------------------------------------------------------- misc

    void SaveLocked()
    {
        Config.Routes = _routes.Select(r => new RouteConfig { From = r.From, To = r.To }).ToList();
        Config.Save(_configPath);
    }

    public void SaveConfig() { lock (_gate) SaveLocked(); }

    void Emit(string s) => Log?.Invoke(s);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watcher?.Stop(); } catch { }
        _refreshDebounce?.Dispose();
        List<Node> virt;
        lock (_gate)
        {
            foreach (var n in _nodes.Values) n.Targets = Array.Empty<Node>();
            foreach (var n in _nodes.Values) CloseLocked(n);
            virt = _nodes.Values.Where(n => n.IsVirtual).ToList();
        }
        try { _session?.Dispose(); } catch { }
        // like loopMIDI: the ports exist only while the patchbay runs
        foreach (var n in virt)
        {
            try { MidiLoopbackManager.RemoveTransientLoopback(new MidiLoopbackRemovalConfig(n.AssociationId)); } catch { }
        }
    }
}

public static class UmpFormat
{
    static readonly string[] Notes = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    static string Note(uint n) => Notes[n % 12] + ((int)(n / 12) - 1);

    public static string Describe(int count, uint w0, uint w1, uint w2, uint w3)
    {
        uint mt = w0 >> 28, grp = ((w0 >> 24) & 0xF) + 1;
        string hex = count switch
        {
            1 => $"{w0:X8}",
            2 => $"{w0:X8} {w1:X8}",
            3 => $"{w0:X8} {w1:X8} {w2:X8}",
            _ => $"{w0:X8} {w1:X8} {w2:X8} {w3:X8}",
        };
        string text = mt switch
        {
            0x1 => System1((w0 >> 16) & 0xFF, (w0 >> 8) & 0x7F, w0 & 0x7F),
            0x2 => Voice1((w0 >> 16) & 0xFF, (w0 >> 8) & 0x7F, w0 & 0x7F),
            0x3 => "SysEx (7-bit) packet",
            0x4 => "MIDI 2.0 " + Voice1(((w0 >> 16) & 0xF0) | ((w0 >> 16) & 0x0F), (w0 >> 8) & 0x7F, 0, midi2: true),
            0x5 => "SysEx (8-bit) / mixed data",
            _ => $"UMP type {mt:X}",
        };
        return $"[G{grp}] {text}   {hex}";
    }

    static string Voice1(uint status, uint d1, uint d2, bool midi2 = false)
    {
        uint ch = (status & 0x0F) + 1;
        string v(uint x) => midi2 ? "" : " " + x;
        return (status & 0xF0) switch
        {
            0x80 => $"Note Off  ch{ch} {Note(d1)}{(midi2 ? "" : " vel " + d2)}",
            0x90 => $"Note On   ch{ch} {Note(d1)}{(midi2 ? "" : " vel " + d2)}",
            0xA0 => $"Poly AT   ch{ch} {Note(d1)}{v(d2)}",
            0xB0 => $"CC        ch{ch} #{d1}{(midi2 ? "" : " = " + d2)}",
            0xC0 => $"Program   ch{ch} {d1}",
            0xD0 => $"Chan AT   ch{ch}{(midi2 ? "" : " " + d1)}",
            0xE0 => midi2 ? $"Pitch Bend ch{ch}" : $"Pitch Bend ch{ch} {((d2 << 7) | d1) - 8192}",
            _ => $"status {status:X2}",
        };
    }

    static string System1(uint st, uint d1, uint d2) => st switch
    {
        0xF1 => $"MTC quarter frame {d1}",
        0xF2 => $"Song Position {(d2 << 7) | d1}",
        0xF3 => $"Song Select {d1}",
        0xF6 => "Tune Request",
        0xF8 => "Clock",
        0xFA => "Start",
        0xFB => "Continue",
        0xFC => "Stop",
        0xFE => "Active Sensing",
        0xFF => "Reset",
        _ => $"System {st:X2}",
    };
}
