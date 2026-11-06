# Testing without tablets

`FakeTablets/` is a second OpenTabletDriver plugin, **for testing only** (never put it in a release). It plugs two
made-up tablets into a real OpenTabletDriver daemon, matched against real configurations that both use
OpenTabletDriver's plain report format and exist unchanged from 0.6.0.4 to 0.6.8.1:

| fake | VID / PID | report | moves |
| --- | --- | --- | --- |
| Gaomon S56K | 9580 / 109 | 8 bytes, device string 201 = `HUION_T156_123456` | a circle, tip pressed 0.3 s every 1.5 s |
| Artisul AP604 | 21827 / 84 | 64 bytes, USB interface 1 | a figure of eight, the tip offset by 0.75 s |

The daemon then treats them exactly as plugged-in tablets: detection, a profile each, the output mode, the area
mapping and the tip binding all run for real.

## Safety

The fake tablets send **nothing** until the file named by the environment variable `FAKE_TABLETS_GO` exists, and
stop after `FAKE_TABLETS_SECONDS` (default 30). Before creating that file, check the daemon's log says
`Output mode: 2Twisted (one cursor per tablet)` for both: on Absolute Mode they would move the real Windows cursor.

Always run the daemon on **scratch settings**, never your own:

- 0.6.8.1: `OpenTabletDriver.Daemon.exe --appdata <scratch folder>`
- 0.6.0.4: `--appdata` moves only the settings file, and plugins are still read from your real folder. Set the
  environment variable `LOCALAPPDATA` to a scratch folder for the daemon process instead; its data folder is then
  `<that folder>\OpenTabletDriver`. (0.6.0.4 also needs the .NET 6 runtime; `DOTNET_ROLL_FORWARD=Major` runs it on a
  newer one.)

To try the board as a file (as macOS and Linux keep it) on Windows, set `TW_PEN_BOARD` to the same file path for
the daemon and the game.

To try the relative output mode, give one profile `TwoTwisted.OtdPlugin.TwoTwistedRelativeMode` instead.

The scratch folder needs `Plugins/TwoTwistedPen/TwoTwistedPen.dll`,
`Plugins/TwoTwistedFakeTablets/TwoTwistedFakeTablets.dll` (the `net6.0` build for 0.6.0.4, `net10.0` for 0.6.8.1),
and a `settings.json` whose profiles for `Gaomon S56K` and `Artisul AP604` already use
`TwoTwisted.OtdPlugin.TwoTwistedOutputMode`, with the tool `TwoTwisted.FakeTablets.FakeTabletsTool` enabled.

## What was found doing this

OpenTabletDriver 0.6.8.1's `RootHub.ConnectDeviceHub` announces a new hub with the old and new device lists the
wrong way round, so the daemon sees no additions and never detects (upstream PR #5111 fixes it). The fake hub is
therefore connected empty and then raises its own `DevicesChanged`, which is announced correctly, the way a real
tablet arrives. 0.6.0.4 does not have the problem.
