# 2Twisted plugin for OpenTabletDriver

Play 2Twisted with **two drawing tablets, one per hand**.

OpenTabletDriver's Absolute Mode moves the Windows cursor, and Windows has only one: two pens on two tablets fight
over it, and the game cannot tell them apart. This plugin adds an output mode, **2Twisted (one cursor per tablet)**,
that moves nothing on the desktop. It sends each tablet's pen straight to the game on the same computer, and the
game gives every tablet a cursor of its own, like two mice.

## Install

1. Use OpenTabletDriver **0.6.0.4 or newer** (the current 0.6.8.1 is recommended).
2. Build or download `TwoTwistedPen.zip`.
3. In OpenTabletDriver: **Plugins → Plugin Manager → Install plugin from file…**, pick `TwoTwistedPen.zip`, and
   restart the driver.
4. For **each** tablet (the tablet list at the top of the OpenTabletDriver window): set **Output Mode** to
   **2Twisted (one cursor per tablet)** and apply.
5. In 2Twisted: **Settings → Input → Pens from OpenTabletDriver** is on by default. Its status line names every
   tablet it hears. Each tablet is listed under **Cursor devices**: move a pen and it takes a hand, or assign the
   hands yourself.

## How it behaves

- **Areas** work as in Absolute Mode. The tablet area is the part of the tablet you use. The display area's
  *shape* is fitted to the game's play area, so a display area the shape of your screen feels exactly like
  Absolute Mode did with the game.
- **The pen tip** (and any pen button bound to a mouse button) is that hand's button in the game.
- **Nothing reaches the desktop** while a tablet is on this output mode. Switch it back to Absolute Mode to use the
  pen anywhere else.
- **Port**: the plugin sends to `127.0.0.1:47290`. Change it only if the game's setting `pen_bridge_port` is
  changed too.

## Limits

- **Two tablets of the same model** look like one tablet to OpenTabletDriver 0.6: it only detects the first one
  (upstream issue OpenTabletDriver#1666). Use two different models. Two identical tablets need a change to the
  driver itself.
- Not yet tried on real tablets. It has been run end to end with two made-up tablets through real OpenTabletDriver
  0.6.0.4 and 0.6.8.1 daemons into the game (see `test/`). If it does not work with yours, start the game with the
  environment variable `TW_PEN_TRACE=1` and send the `[PenBridge]` lines from its log.

## Build

```powershell
./package.ps1
```

Builds `src/` against the OpenTabletDriver 0.6.0.4 plugin library (.NET 6, so one build loads in every 0.6 release
from 0.6.0.4 on) and writes `dist/TwoTwistedPen.zip`.

## The packet

One UDP datagram per pen report, to 127.0.0.1, little-endian. The game's reader is `scripts/Input/PenBridgeSource.cs`
in the 2Twisted repository; `src/PenPacket.cs` here is the writer, and the two must agree byte for byte.

| offset | size | field |
| --- | --- | --- |
| 0 | 4 | magic `2TWP` |
| 4 | 1 | version (1) |
| 5 | 1 | flags: 1 in range, 2 tip, 4 pen button 1, 8 pen button 2, 0x80 goodbye |
| 6 | 2 | length of the tablet name in bytes |
| 8 | 4 | source id: FNV-1a of the tablet name |
| 12 | 4 | sequence number, per tablet |
| 16 | 4 | x, float: 0 to 1 across the display area |
| 20 | 4 | y, float |
| 24 | 4 | pressure, float, 0 to 1 |
| 28 | 4 | display area width, pixels |
| 32 | 4 | display area height, pixels |
| 36 | n | tablet name, UTF-8 |
