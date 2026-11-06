# 2Twisted plugin for OpenTabletDriver

Play 2Twisted with **two drawing tablets, one per hand**.

OpenTabletDriver's own output modes move the Windows cursor, and Windows has only one: two pens on two tablets
fight over it, and the game cannot tell them apart. This plugin adds two output modes that move nothing on the
desktop. Each writes its tablet's pen into a small block of memory the game also reads, and the game gives every
tablet a cursor of its own, like two mice.

## Install

1. Use OpenTabletDriver **0.6.0.4 or newer** (the current 0.6.8.1 is recommended).
2. Build or download `TwoTwistedPen.zip`.
3. In OpenTabletDriver: **Plugins → Plugin Manager → Install plugin from file…**, pick `TwoTwistedPen.zip`, and
   restart the driver.
4. For **each** tablet (the tablet list at the top of the OpenTabletDriver window), set **Output Mode** to one of:
   - **2Twisted (one cursor per tablet)**: absolute, like Absolute Mode.
   - **2Twisted relative (one cursor per tablet)**: like Relative Mode, the pen pushes its cursor the way a mouse
     does.
5. In 2Twisted: **Settings → Input → Pens from OpenTabletDriver** is on by default. Its status line names every
   tablet whose pen has moved. Each tablet is listed under **Cursor devices**: move a pen and it takes a hand, or
   assign the hands yourself.

## How it behaves

- **Absolute**: the tablet area is the part of the tablet you use, and the display area's *shape* is fitted to the
  game's play area, so a display area the shape of your screen feels exactly like Absolute Mode did with the game.
- **Relative**: speed is the profile's relative sensitivity, then the game's mouse sensitivity for that hand
  (including its per-hand multiplier). Every movement reaches the game, however many reports arrive between two
  frames.
- **The pen tip** (and any pen button bound to a mouse button) is that hand's button. A tap shorter than a frame
  is still a click.
- **Nothing reaches the desktop** while a tablet is on one of these modes. Switch it back to Absolute or Relative
  Mode to use the pen anywhere else.
- **No network.** The plugin and the game meet in shared memory: no port, no socket, nothing the firewall is asked
  about.

## Limits

- **Two tablets of the same model** look like one tablet to OpenTabletDriver 0.6: it only detects the first one
  (upstream issue OpenTabletDriver#1666). Use two different models. Two identical tablets need a change to the
  driver itself.
- Not yet tried on real tablets. It has been run end to end with two made-up tablets through real OpenTabletDriver
  0.6.0.4 and 0.6.8.1 daemons into the game (see `test/`). OpenTabletDriver's log says `Pen board open` once the
  plugin has reached the board. If it does not work, start the game with the environment variable
  `TW_PEN_TRACE=1` and send the `[PenBridge]` lines from its log.

## Build

```powershell
./package.ps1
```

Builds `src/` against the OpenTabletDriver 0.6.0.4 plugin library (.NET 6, so one build loads in every 0.6 release
from 0.6.0.4 on) and writes `dist/TwoTwistedPen.zip`.

## The board

On Windows the named shared memory `Local\2TwistedPens` (this login session; made by whichever of OpenTabletDriver
and the game starts first). Elsewhere the file `<temp>/2twisted-pens.shm`, since .NET maps names only on Windows.
4160 bytes, little-endian. The game's reader is `scripts/Input/PenBridgeSource.cs` in the 2Twisted repository;
`src/PenBoard.cs` here is the writer, and the two must agree byte for byte.

Header, 64 bytes: magic `2TWS` at 0, version (1) at 4, slot count (16) at 8, slot size (256) at 12.

Slot *i* at 64 + *i* × 256, one per tablet:

| offset | size | field |
| --- | --- | --- |
| 0 | 4 | sequence: odd while being written, even when whole (a seqlock: a reader that sees it change reads again) |
| 4 | 4 | source id: FNV-1a of the tablet name; 0 = free |
| 8 | 4 | flags: 1 in range, 2 tip, 4 pen button 1, 8 pen button 2, 0x10 relative, 0x80 goodbye |
| 12 | 4 | presses: button-downs ever (so a tap between two frames is never lost) |
| 16 | 4 | x, float: 0 to 1 across the display area (absolute) |
| 20 | 4 | y, float |
| 24 | 4 | pressure, float, 0 to 1 |
| 28 | 4 | display area width, pixels |
| 32 | 4 | display area height, pixels |
| 36 | 4 | name length in bytes |
| 40 | ≤200 | tablet name, UTF-8 |
| 240 | 8 | total x, double: a relative pen's movement, added up (pixels) |
| 248 | 8 | total y, double |

Each slot holds its pen's latest state rather than a queue: the game reads every device once a frame, and the two
counters (presses, and the relative totals) carry everything that happened in between.
