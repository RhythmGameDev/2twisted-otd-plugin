using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using OpenTabletDriver.Plugin;

namespace TwoTwisted.OtdPlugin
{
    // THE PENS' SHARED BOARD: a small block of memory both OpenTabletDriver (this plugin) and 2Twisted map, where
    // each tablet has a slot holding its pen's latest state. The plugin writes a slot the moment a pen report has
    // been mapped; the game reads every slot once a frame, at the same point it reads the mice. No socket, no
    // port, nothing a firewall is asked about.
    //
    // WHERE: on Windows the named mapping "Local\2TwistedPens" (this login session; created by whichever of the
    // two starts first, gone when both have closed). Elsewhere, a file of the same layout in the temporary folder
    // (2twisted-pens.shm), since .NET maps names only on Windows.
    //
    // THE LAYOUT (little-endian; the game's reader is scripts/Input/PenBridgeSource.cs in the 2Twisted repository,
    // and the two must agree byte for byte):
    //   header, 64 bytes: 0 magic "2TWS" · 4 version (1) · 8 slot count (16) · 12 slot size (256)
    //   slot i at 64 + i * 256:
    //     0  sequence: odd while being written, even when whole (a "seqlock": a reader that sees it change, or
    //        odd, reads again, so it never takes half of one write and half of the next)
    //     4  source id: FNV-1a of the tablet's name; 0 = a free slot
    //     8  flags: 1 in range, 2 tip down, 4 pen button 1, 8 pen button 2, 0x10 relative, 0x80 goodbye
    //     12 presses: how many times a button has gone down, ever. The board holds the LATEST state, so a tap
    //        that goes down and up between two of the game's frames would otherwise be lost; this never is
    //     16 x · 20 y (0 to 1 across the display area) · 24 pressure (0 to 1) · 28 display area width ·
    //        32 height (pixels), floats
    //     36 length of the tablet's name in bytes · 40 the name, UTF-8, at most 200 bytes
    //     240 total x · 248 total y, doubles: for a RELATIVE pen (flag 0x10), every movement OpenTabletDriver has
    //        reported, added up (pixels, after its own sensitivity). Totals rather than a queue: the game moves the
    //        cursor by the change since its last frame, so movement between two frames is never lost, however many
    //        reports it took, and the board stays one slot per tablet. An absolute pen leaves them at 0.
    public static class PenBoard
    {
        public const string WindowsName = @"Local\2TwistedPens";
        public const string FileName = "2twisted-pens.shm";
        public const int Magic = 0x53575432;   // "2TWS"
        public const int Version = 1;
        public const int HeaderSize = 64, SlotCount = 16, SlotSize = 256, MaxNameBytes = 200;
        public const int Size = HeaderSize + SlotCount * SlotSize;
        public const int InRange = 0x01, Tip = 0x02, Button1 = 0x04, Button2 = 0x08, Relative = 0x10, Goodbye = 0x80;

        // Slot field offsets.
        public const int OSequence = 0, OSource = 4, OFlags = 8, OPresses = 12, OX = 16, OY = 20, OPressure = 24,
            OAreaW = 28, OAreaH = 32, ONameLength = 36, OName = 40, OTotalX = 240, OTotalY = 248;

        private static readonly object OpenLock = new();
        private static readonly object[] SlotLocks = CreateLocks();
        private static MemoryMappedFile? _map;
        private static MemoryMappedViewAccessor? _view;
        private static long _retryAtMs;

        private static object[] CreateLocks()
        {
            var locks = new object[SlotCount];
            for (int i = 0; i < SlotCount; i++) locks[i] = new object();
            return locks;
        }

        public static uint SourceId(string name)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(name ?? ""))
            {
                h ^= b;
                h *= 16777619;
            }
            return h == 0 ? 1 : h;   // 0 means a free slot
        }

        // The board, opened (or made) on first use. A failure is retried a few seconds later, never per report.
        private static MemoryMappedViewAccessor? View()
        {
            if (_view != null) return _view;
            lock (OpenLock)
            {
                if (_view != null) return _view;
                long now = Environment.TickCount64;
                if (now < _retryAtMs) return null;
                try
                {
                    _map = OpenMap();
                    var view = _map.CreateViewAccessor(0, Size, MemoryMappedFileAccess.ReadWrite);
                    if (view.ReadInt32(0) != Magic)
                    {
                        view.Write(4, Version);
                        view.Write(8, SlotCount);
                        view.Write(12, SlotSize);
                        Thread.MemoryBarrier();
                        view.Write(0, Magic);
                    }
                    _view = view;
                    Log.Write("2Twisted", $"Pen board open ({Where})");
                }
                catch (Exception ex)
                {
                    _retryAtMs = now + 3000;
                    Log.Write("2Twisted", $"Could not open the pen board ({Where}): {ex.GetType().Name}: {ex.Message}", LogLevel.Warning);
                }
                return _view;
            }
        }

        private static string Where => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsName : Path.Combine(Path.GetTempPath(), FileName);

        private static MemoryMappedFile OpenMap()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return MemoryMappedFile.CreateOrOpen(WindowsName, Size, MemoryMappedFileAccess.ReadWrite);
            var path = Path.Combine(Path.GetTempPath(), FileName);
            var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (fs.Length < Size) fs.SetLength(Size);
            return MemoryMappedFile.CreateFromFile(fs, null, Size, MemoryMappedFileAccess.ReadWrite, HandleInheritability.None, leaveOpen: false);
        }

        // This tablet's slot: the one already holding its id (it keeps its slot across a restart of the driver),
        // else the first free one. -1 when the board cannot be opened or all sixteen are taken.
        public static int Claim(uint source)
        {
            MemoryMappedViewAccessor? v = View();
            if (v == null) return -1;
            lock (OpenLock)
            {
                int free = -1;
                for (int i = 0; i < SlotCount; i++)
                {
                    uint id = v.ReadUInt32(HeaderSize + i * SlotSize + OSource);
                    if (id == source) return i;
                    if (id == 0 && free < 0) free = i;
                }
                if (free >= 0) v.Write(HeaderSize + free * SlotSize + OSource, source);
                return free;
            }
        }

        // A relative pen's totals as the board has them (0, 0 for a tablet it has never held), so a new output mode,
        // or a restarted driver, carries on counting instead of starting again from 0 under the game's feet.
        public static (double X, double Y) ReadTotals(uint source)
        {
            int slot = Claim(source);
            MemoryMappedViewAccessor? v = View();
            if (v == null || slot < 0) return (0, 0);
            long s = HeaderSize + (long)slot * SlotSize;
            lock (SlotLocks[slot])
            {
                double x = v.ReadDouble(s + OTotalX), y = v.ReadDouble(s + OTotalY);
                return (double.IsFinite(x) ? x : 0, double.IsFinite(y) ? y : 0);
            }
        }

        public static void Write(int slot, uint source, int flags, uint presses, float x, float y, float pressure,
            float areaW, float areaH, double totalX, double totalY, byte[] name)
        {
            MemoryMappedViewAccessor? v = View();
            if (v == null || slot < 0 || slot >= SlotCount) return;
            long s = HeaderSize + (long)slot * SlotSize;
            lock (SlotLocks[slot])
            {
                uint seq = v.ReadUInt32(s + OSequence);
                if ((seq & 1) != 0) seq++;   // a write that never finished (the driver was killed mid-write)
                v.Write(s + OSequence, seq + 1);   // odd: being written
                Thread.MemoryBarrier();
                v.Write(s + OSource, source);
                v.Write(s + OFlags, flags);
                v.Write(s + OPresses, presses);
                v.Write(s + OX, x);
                v.Write(s + OY, y);
                v.Write(s + OPressure, pressure);
                v.Write(s + OAreaW, areaW);
                v.Write(s + OAreaH, areaH);
                int n = Math.Min(name.Length, MaxNameBytes);
                v.Write(s + ONameLength, n);
                v.WriteArray(s + OName, name, 0, n);
                v.Write(s + OTotalX, totalX);
                v.Write(s + OTotalY, totalY);
                Thread.MemoryBarrier();
                v.Write(s + OSequence, seq + 2);   // even: whole
            }
        }
    }
}
