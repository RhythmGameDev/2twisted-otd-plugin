using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;

namespace TwoTwisted.OtdPlugin
{
    // ONE TABLET'S PEN, AS THE GAME WILL SEE IT, for either output mode: which slot of the board it writes, its
    // buttons and how many times they have gone down, its pressure, and whether it is over the tablet. The two
    // pointers below add only what their mode means by "where": a position in the display area (absolute) or a
    // running total of movement (relative).
    internal sealed class PenChannel
    {
        private readonly Func<string> _tabletName;
        private readonly object _lock = new();
        private int _buttons;
        private uint _presses;
        private bool _inRange;
        private string? _name;
        private byte[] _nameBytes = Array.Empty<byte>();
        private uint _source;
        private int _slot = -1;

        public float Pressure;

        public PenChannel(Func<string> tabletName) => _tabletName = tabletName;

        public bool InRange => _inRange;

        // The tip and pen buttons, from OpenTabletDriver's bindings (a binding to a mouse button calls the output
        // mode's pointer). Returns true when that changed anything, so the pointer can publish at once.
        public bool SetButton(MouseButton button, bool down)
        {
            int bit = button switch
            {
                MouseButton.Left => PenBoard.Tip,
                MouseButton.Right => PenBoard.Button1,
                MouseButton.Middle => PenBoard.Button2,
                _ => 0,
            };
            if (bit == 0) return false;
            lock (_lock)
            {
                int before = _buttons;
                _buttons = down ? _buttons | bit : _buttons & ~bit;
                if (down && before == 0 && _buttons != 0) _presses++;
                return before != _buttons;
            }
        }

        public void UpdatePressure(IDeviceReport report, TabletReference? tablet)
        {
            if (report is not ITabletReport t) return;
            uint max = tablet?.Properties?.Specifications?.Pen?.MaxPressure ?? 0;
            Pressure = max > 0 ? Math.Clamp(t.Pressure / (float)max, 0f, 1f) : 0f;
        }

        public void Publish(bool inRange, bool goodbye, bool relative, Vector2 position, Vector2 area, double totalX, double totalY)
        {
            lock (_lock)
            {
                // The tablet's name is known once OpenTabletDriver has set the output mode's tablet; it is encoded,
                // and the slot claimed, once per name rather than per report.
                string name = _tabletName();
                if (!ReferenceEquals(name, _name))
                {
                    _name = name;
                    _nameBytes = Encoding.UTF8.GetBytes(name);
                    _source = PenBoard.SourceId(name);
                    _slot = -1;
                }
                if (_slot < 0) _slot = PenBoard.Claim(_source);
                if (_slot < 0) return;

                _inRange = inRange && !goodbye;
                int flags = goodbye
                    ? PenBoard.Goodbye
                    : (_inRange ? PenBoard.InRange : 0) | _buttons | (relative ? PenBoard.Relative : 0);
                PenBoard.Write(_slot, _source, flags, _presses, position.X, position.Y, Pressure, area.X, area.Y,
                    totalX, totalY, _nameBytes);
            }
        }
    }

    // Absolute: OpenTabletDriver calls SetPosition with the pen mapped into the display area, in desktop pixels.
    internal sealed class AbsolutePenPointer : IAbsolutePointer, IMouseButtonHandler
    {
        private readonly TwoTwistedOutputMode _mode;
        private readonly object _lock = new();
        private Vector2 _position;

        public readonly PenChannel Channel;

        public AbsolutePenPointer(TwoTwistedOutputMode mode)
        {
            _mode = mode;
            Channel = new PenChannel(() => mode.Tablet?.Properties?.Name ?? "Tablet");
        }

        public void SetPosition(Vector2 pos)
        {
            lock (_lock) _position = pos;
        }

        public void MouseDown(MouseButton button) { if (Channel.SetButton(button, true)) Publish(Channel.InRange); }
        public void MouseUp(MouseButton button) { if (Channel.SetButton(button, false)) Publish(Channel.InRange); }

        public void Publish(bool inRange, bool goodbye = false)
        {
            (Vector2 min, Vector2 size) = _mode.DisplayArea();
            Vector2 n;
            lock (_lock) n = (_position - min) / size;
            Channel.Publish(inRange, goodbye, relative: false, n, size, 0, 0);
        }
    }

    // Relative: OpenTabletDriver calls SetPosition with the MOVEMENT since its last report (pixels, after the
    // profile's sensitivity; its own reset after the pen is lifted keeps a new contact from jumping). Kept as
    // running totals, so the game can never miss any of it.
    internal sealed class RelativePenPointer : IRelativePointer, IMouseButtonHandler
    {
        // One running total per TABLET, not per output mode: OpenTabletDriver makes a new output mode every time
        // its settings are applied, and a total that started again from 0 would read as a huge movement. The first
        // use in this process starts from what the board already holds, so a restarted driver carries on too.
        private static readonly Dictionary<uint, double[]> Totals = new();

        private readonly TwoTwistedRelativeMode _mode;

        public readonly PenChannel Channel;

        public RelativePenPointer(TwoTwistedRelativeMode mode)
        {
            _mode = mode;
            Channel = new PenChannel(() => mode.Tablet?.Properties?.Name ?? "Tablet");
        }

        private double[] MyTotals()
        {
            uint source = PenBoard.SourceId(_mode.Tablet?.Properties?.Name ?? "Tablet");
            lock (Totals)
            {
                if (!Totals.TryGetValue(source, out double[]? t))
                {
                    (double x, double y) = PenBoard.ReadTotals(source);
                    Totals[source] = t = new[] { x, y };
                }
                return t;
            }
        }

        public void SetPosition(Vector2 delta)
        {
            if (!float.IsFinite(delta.X) || !float.IsFinite(delta.Y)) return;
            double[] t = MyTotals();
            lock (t)
            {
                t[0] += delta.X;
                t[1] += delta.Y;
            }
        }

        public void MouseDown(MouseButton button) { if (Channel.SetButton(button, true)) Publish(Channel.InRange); }
        public void MouseUp(MouseButton button) { if (Channel.SetButton(button, false)) Publish(Channel.InRange); }

        public void Publish(bool inRange, bool goodbye = false)
        {
            double[] t = MyTotals();
            double x, y;
            lock (t) { x = t[0]; y = t[1]; }
            Channel.Publish(inRange, goodbye, relative: true, Vector2.Zero, Vector2.Zero, x, y);
        }
    }
}
