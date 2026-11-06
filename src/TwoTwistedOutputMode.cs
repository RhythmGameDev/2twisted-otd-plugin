using System;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;

namespace TwoTwisted.OtdPlugin
{
    // AN OUTPUT MODE THAT GIVES EACH TABLET ITS OWN CURSOR IN 2TWISTED.
    //
    // OpenTabletDriver's own Absolute Mode moves the Windows cursor, and Windows has one cursor: two pens on two
    // tablets end up fighting over it, and the game cannot tell them apart (each arrives with no device on it).
    // This mode moves nothing on the desktop. It sends each tablet's pen, by name, straight to the game on this
    // computer, which gives every tablet a device of its own: one pen per hand.
    //
    // HOW TO USE: set the Output Mode of EACH tablet to "2Twisted (one cursor per tablet)". The tablet area and the
    // display area work as they do in Absolute Mode: the tablet area is the part of the tablet you use, and the
    // display area's shape is fitted to the play area (a display area the shape of your screen behaves exactly as
    // Absolute Mode does with the game). The pen tip, and any pen button bound to a mouse button, is that hand's
    // button. Nothing reaches the desktop, so switch back to Absolute Mode to use the pen anywhere else.
    //
    // OpenTabletDriver makes one of these per tablet, and every packet carries its tablet's name, so two
    // tablets of different models are two devices. (Two of the same model are one tablet to OpenTabletDriver
    // 0.6, which only detects the first: that needs a change to the driver itself.)
    [PluginName(PluginTitle)]
    public class TwoTwistedOutputMode : AbsoluteOutputMode, IDisposable
    {
        public const string PluginTitle = "2Twisted (one cursor per tablet)";
        public const int DefaultPort = 47290;

        private readonly PenPointer _pointer;

        public TwoTwistedOutputMode()
        {
            _pointer = new PenPointer(this);
        }

        // Not [Resolved]: the pointer is this mode's own, not the shared desktop one (OpenTabletDriver hands
        // every tablet the same desktop pointer, which is the whole problem this plugin exists to get around).
        public override IAbsolutePointer Pointer
        {
            get => _pointer;
            set { }
        }

        [Property("Port"), DefaultPropertyValue(DefaultPort),
         ToolTip("The port 2Twisted listens on. Leave it at 47290 unless the game's settings say otherwise.")]
        public int Port { set; get; } = DefaultPort;

        protected override void OnOutput(IDeviceReport report)
        {
            base.OnOutput(report);   // the transformed position reaches PenPointer.SetPosition here

            if (report is ITabletReport tablet)
            {
                uint max = Tablet?.Properties?.Specifications?.Pen?.MaxPressure ?? 0;
                _pointer.Pressure = max > 0 ? Math.Clamp(tablet.Pressure / (float)max, 0f, 1f) : 0f;
            }

            // A pen report is "in range"; the driver's out-of-range report (0.6.5 and later; the type is named
            // rather than referenced so this builds against 0.6.0.4) says the pen has gone. Anything else (the
            // tablet's own buttons) says nothing about the pen.
            if (report is IAbsolutePositionReport)
                _pointer.Send(inRange: true);
            else if (report.GetType().Name == "OutOfRangeReport")
                _pointer.Send(inRange: false);
        }

        // Called by OpenTabletDriver 0.6.8 and later when settings are applied or the tablet goes away (earlier
        // 0.6 releases never dispose an output mode). Tells the game this tablet's cursor is gone.
        // Explicit, so it also answers IDisposable on 0.6.8, whose OutputMode has a Dispose of its own that this
        // build (against 0.6.0.4) cannot see: re-implementing the interface here is what makes this one called.
        void IDisposable.Dispose() => _pointer.Send(inRange: false, goodbye: true);

        // The display area as the game should fit it: its rectangle in desktop pixels.
        internal (Vector2 Min, Vector2 Size) DisplayArea()
        {
            Area? o = Output;
            if (o == null || o.Width <= 0 || o.Height <= 0) return (Vector2.Zero, Vector2.One);
            var size = new Vector2(o.Width, o.Height);
            return (o.Position - size / 2, size);
        }

        internal string TabletName => Tablet?.Properties?.Name ?? "Tablet";

        // One socket for every tablet: a datagram send is safe from the two tablets' threads at once, and a
        // socket per output mode would leak one each time settings are applied on releases that never dispose.
        private static readonly object SocketLock = new();
        private static Socket? _socket;

        internal void SendBytes(byte[] packet)
        {
            try
            {
                Socket s;
                lock (SocketLock)
                    s = _socket ??= new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.SendTo(packet, new IPEndPoint(IPAddress.Loopback, Port is > 0 and < 65536 ? Port : DefaultPort));
            }
            catch (SocketException)
            {
                // The game is not running, or nothing listens on the port: a pen moving with the game closed
                // is not an error.
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    // The pen as the game will see it: where it is, how hard it presses, and which of its buttons are down.
    // OpenTabletDriver calls SetPosition (after the area mapping) and MouseDown / MouseUp (from the tip and pen
    // button bindings); the output mode sends one packet per pen report, and one more whenever a button changes.
    internal sealed class PenPointer : IAbsolutePointer, IMouseButtonHandler
    {
        private readonly TwoTwistedOutputMode _mode;
        private readonly object _lock = new();
        private Vector2 _position;
        private byte _buttons;
        private uint _sequence;
        private bool _inRange;

        public float Pressure;

        public PenPointer(TwoTwistedOutputMode mode) => _mode = mode;

        public void SetPosition(Vector2 pos)
        {
            lock (_lock) _position = pos;
        }

        public void MouseDown(MouseButton button) => SetButton(button, true);
        public void MouseUp(MouseButton button) => SetButton(button, false);

        private void SetButton(MouseButton button, bool down)
        {
            byte bit = button switch
            {
                MouseButton.Left => PenPacket.Tip,
                MouseButton.Right => PenPacket.Button1,
                MouseButton.Middle => PenPacket.Button2,
                _ => 0,
            };
            if (bit == 0) return;
            bool changed;
            lock (_lock)
            {
                byte before = _buttons;
                _buttons = down ? (byte)(_buttons | bit) : (byte)(_buttons & ~bit);
                changed = before != _buttons;
            }
            // A click is sent the moment it happens, not with the next position report.
            if (changed) Send(_inRange);
        }

        public void Send(bool inRange, bool goodbye = false)
        {
            byte[] packet;
            lock (_lock)
            {
                _inRange = inRange && !goodbye;
                (Vector2 min, Vector2 size) = _mode.DisplayArea();
                Vector2 n = (_position - min) / size;
                byte flags = (byte)((_inRange ? PenPacket.InRange : 0) | _buttons | (goodbye ? PenPacket.Goodbye : 0));
                if (goodbye) flags = PenPacket.Goodbye;
                packet = PenPacket.Encode(_mode.TabletName, ++_sequence, flags, n.X, n.Y, Pressure, size.X, size.Y);
            }
            _mode.SendBytes(packet);
        }
    }
}
