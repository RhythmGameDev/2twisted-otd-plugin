using System;
using System.Buffers.Binary;
using System.Text;

namespace TwoTwisted.OtdPlugin
{
    // ONE PEN'S STATE, AS ONE UDP DATAGRAM to the game on this computer (127.0.0.1). The game's reader is
    // scripts/Input/PenBridgeSource.cs in the 2Twisted repository, and the two must agree on every byte below.
    // Little-endian throughout.
    //
    //   offset  size  field
    //   0       4     magic "2TWP"
    //   4       1     version (1)
    //   5       1     flags: 1 in range, 2 tip down, 4 pen button 1, 8 pen button 2, 0x80 goodbye
    //   6       2     length of the tablet name in bytes
    //   8       4     source id: FNV-1a of the tablet name, so each tablet is its own device in the game
    //   12      4     sequence number, per source
    //   16      4     x, float: 0 to 1 across the display area set in OpenTabletDriver (past it, unless clipped)
    //   20      4     y, float
    //   24      4     pressure, float, 0 to 1
    //   28      4     display area width in pixels, float (the game fits this shape to the play area)
    //   32      4     display area height in pixels, float
    //   36      n     tablet name, UTF-8
    public static class PenPacket
    {
        public const byte Version = 1;
        public const int HeaderSize = 36;
        public const int MaxNameBytes = 200;

        public const byte InRange = 0x01, Tip = 0x02, Button1 = 0x04, Button2 = 0x08, Goodbye = 0x80;

        public static readonly byte[] Magic = { (byte)'2', (byte)'T', (byte)'W', (byte)'P' };

        public static uint SourceId(string name)
        {
            uint h = 2166136261;
            foreach (byte b in Encoding.UTF8.GetBytes(name ?? ""))
            {
                h ^= b;
                h *= 16777619;
            }
            return h;
        }

        public static byte[] Encode(string name, uint sequence, byte flags, float x, float y, float pressure,
            float areaWidth, float areaHeight)
        {
            byte[] nameBytes = Encoding.UTF8.GetBytes(name ?? "");
            if (nameBytes.Length > MaxNameBytes) Array.Resize(ref nameBytes, MaxNameBytes);
            var p = new byte[HeaderSize + nameBytes.Length];
            Magic.CopyTo(p, 0);
            p[4] = Version;
            p[5] = flags;
            BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(6), (ushort)nameBytes.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(8), SourceId(name ?? ""));
            BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(12), sequence);
            WriteFloat(p, 16, x);
            WriteFloat(p, 20, y);
            WriteFloat(p, 24, pressure);
            WriteFloat(p, 28, areaWidth);
            WriteFloat(p, 32, areaHeight);
            nameBytes.CopyTo(p, HeaderSize);
            return p;
        }

        private static void WriteFloat(byte[] p, int at, float v)
            => BinaryPrimitives.WriteInt32LittleEndian(p.AsSpan(at), BitConverter.SingleToInt32Bits(v));
    }
}
