# MIDI Patchbay

A loopMIDI-style virtual MIDI port creator with a routing matrix, for Windows 11 25H2+.

- **Virtual ports**: type a name and click **Add port**. Every app (DAWs, synths, MIDI 1.0 and
  MIDI 2.0) sees it as a MIDI input *and* output with that name.
- **loopMIDI behaviour**: a new port is routed to itself, so whatever app A sends to `Port`,
  app B receives from `Port`.
- **Routing matrix**: rows are sources, columns are destinations. Click a cell to connect.
  Route virtual ports to each other, to hardware, or hardware to virtual ports. One source can feed
  many destinations (split) and many sources can feed one destination (merge).
- **Activity lights and counters** show traffic, and the **monitor** decodes messages.
- Ports exist while the app runs. Closing the window keeps it in the notification area;
  **Exit** from the tray menu removes the ports. Tray menu has **Start with Windows**.
- **Dark mode**: Theme menu in the status bar (or the tray menu): System, Light or Dark.
  *System* follows the Windows app theme and switches live.
- Settings: `%APPDATA%\MidiPatchbay\config.json`.

## How it works

No driver is installed. Each port is a *transient loopback pair* in **Windows MIDI Services**
(the `midisrv` service built into Windows 11 25H2):

| side | name | who uses it |
|------|------|-------------|
| A | `Port` | every app (MIDI 1.0 port + MIDI 2.0 endpoint) |
| B | `Port (patchbay)` | UMP-only, hidden from MIDI 1.0 apps; opened by the patchbay |

Messages apps send to `Port` arrive on side B; the patchbay forwards them per the matrix by
sending to the destination's B side (or to a hardware endpoint). MIDI 2.0 utility/stream
messages are not forwarded, and messages going into a virtual port are put on group 1.

## Build / run

Requires the .NET 8 SDK (x64). The Windows MIDI Services SDK (`Windows.Devices.Midi2`,
preview 11, MIT) is vendored in `MidiPatchbay/sdk/`; C# bindings are generated at build time by CsWinRT.

```
cd MidiPatchbay
dotnet build -c Release
bin\Release\net8.0-windows10.0.22621.0\win-x64\MidiPatchbay.exe
```

Options: `--minimized` (start in tray), `--config <path>`, `--selftest` (creates two test
ports, sends notes through them with the classic WinMM API, and checks loop, routing,
unrouting and cleanup).

## Limits

- Windows 11 25H2 or later only (needs Windows MIDI Services).
- The SDK is a Microsoft preview; a future Windows update could change it.
- Apps that are already running may need to rescan MIDI devices to see a new port.
- No per-route channel filtering or transforms yet.
