using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Components;
using OpenTabletDriver.Plugin.DependencyInjection;
using OpenTabletDriver.Plugin.Devices;

namespace TwoTwisted.FakeTablets
{
    // TWO MADE-UP TABLETS, plugged into a real OpenTabletDriver daemon (0.6.0.4 or 0.6.8.1), for testing without hardware.
    //
    // OpenTabletDriver 0.6 only reads device hubs from its own assembly, so this tool connects one by hand: the
    // driver behind IDriver keeps its CompositeDeviceHub as a public property that the plugin interface does not
    // expose, and is reached by reflection. Connecting it raises DevicesChanged, the daemon detects tablets again,
    // and each fake tablet is matched against a real configuration, exactly as if it had been plugged in:
    //   - "Gaomon S56K"  (VID 9580 / PID 109, 8-byte reports, device string 201 = HUION_T156_<6 digits>)
    //   - "Artisul AP604" (VID 21827 / PID 84, 64-byte reports, USB interface 1, which 0.6.8.1 asks for)
    // (Both configurations exist, unchanged, in every release from 0.6.0.4 to 0.6.8.1.)
    // Both configurations use OpenTabletDriver's plain TabletReportParser: byte 1 the pen buttons, X at 2, Y at
    // 4 and pressure at 6, all little-endian shorts.
    //
    // SILENT UNTIL TOLD: the tablets send nothing until the file named by FAKE_TABLETS_GO exists, so the test can
    // first check in the log that both are on the 2Twisted output mode. On Absolute Mode they would move the
    // real Windows cursor. They stop again after FAKE_TABLETS_SECONDS (default 30) seconds of movement.
    [PluginName("2Twisted test: two fake tablets")]
    public class FakeTabletsTool : ITool
    {
        private static FakeHub? _hub;

        [Resolved]
        public IDriver? Driver { set; get; }

        public bool Initialize()
        {
            if (Driver?.GetType().GetProperty("CompositeDeviceHub")?.GetValue(Driver) is not ICompositeDeviceHub composite)
            {
                Log.Write("FakeTablets", "The driver has no CompositeDeviceHub: this OpenTabletDriver is not 0.6.x.", LogLevel.Error);
                return false;
            }
            // Settings are applied again when the tablets appear, which disposes and remakes every tool: the hub
            // is connected once and kept.
            if (_hub == null)
            {
                // Connected EMPTY, then plugged in. OpenTabletDriver 0.6.8.1's ConnectDeviceHub announces the change
                // with the old and new lists the wrong way round (RootHub.CommitHubChange; upstream PR #5111 fixes
                // it), so the daemon sees no additions and never detects. A hub's own DevicesChanged is announced
                // correctly, and that is how a real tablet arrives too.
                _hub = new FakeHub();
                composite.ConnectDeviceHub(_hub);
                _hub.PlugIn();
                Log.Write("FakeTablets", "Two fake tablets connected (Gaomon S56K, Artisul AP604). Waiting for "
                    + (Environment.GetEnvironmentVariable("FAKE_TABLETS_GO") ?? "(FAKE_TABLETS_GO not set)"));
            }
            return true;
        }

        public void Dispose()
        {
        }
    }

    internal sealed class FakeHub : IDeviceHub
    {
        private readonly IDeviceEndpoint[] _devices =
        {
            new FakeTablet("Gaomon S56K (fake)", 9580, 109, 8, 32000, 24000, 2047, left: true),
            new FakeTablet("Artisul AP604 (fake)", 21827, 84, 64, 15900, 9930, 2047, left: false, usbInterface: "1"),
        };
        private IDeviceEndpoint[] _current = Array.Empty<IDeviceEndpoint>();

        public event EventHandler<DevicesChangedEventArgs>? DevicesChanged;

        public IEnumerable<IDeviceEndpoint> GetDevices() => _current;

        public void PlugIn()
        {
            var before = _current;
            _current = _devices;
            DevicesChanged?.Invoke(this, new DevicesChangedEventArgs(before, _current));
        }
    }

    internal sealed class FakeTablet : IDeviceEndpoint
    {
        private readonly int _maxX, _maxY, _maxPressure;
        private readonly bool _left;

        public FakeTablet(string name, int vid, int pid, int reportLength, int maxX, int maxY, int maxPressure, bool left,
            string? usbInterface = null)
        {
            if (usbInterface != null) DeviceAttributes["USB_INTERFACE_NUMBER"] = usbInterface;
            ProductName = FriendlyName = name;
            VendorID = vid;
            ProductID = pid;
            InputReportLength = reportLength;
            _maxX = maxX;
            _maxY = maxY;
            _maxPressure = maxPressure;
            _left = left;
            DevicePath = $"fake://2twisted/{vid:X4}:{pid:X4}";
        }

        public int ProductID { get; }
        public int VendorID { get; }
        public int InputReportLength { get; }
        public int OutputReportLength => 0;
        public int FeatureReportLength => 0;
        public string? Manufacturer => "2Twisted test";
        public string? ProductName { get; }
        public string? FriendlyName { get; }
        public string? SerialNumber => null;
        public string DevicePath { get; }
        public bool CanOpen => true;
        public IDictionary<string, string> DeviceAttributes { get; } = new Dictionary<string, string>();

        public IDeviceEndpointStream Open() => new FakeStream(this);

        // The Gaomon's configuration identifies it by string 201; the initialization strings are only read.
        public string? GetDeviceString(byte index) => index == 201 ? "HUION_T156_123456" : "2TWISTED_FAKE";

        public void OpenAndSendFeatureInit(byte[] initData) { }
        public void OpenAndSendOutputInit(byte[] initData) { }

        private sealed class FakeStream : IDeviceEndpointStream
        {
            private readonly FakeTablet _t;
            private readonly string? _goFile = Environment.GetEnvironmentVariable("FAKE_TABLETS_GO");
            private readonly double _seconds = double.TryParse(Environment.GetEnvironmentVariable("FAKE_TABLETS_SECONDS"), out double s) ? s : 30;
            private DateTime _start = DateTime.MinValue;
            private bool _disposed;

            public FakeStream(FakeTablet t) => _t = t;

            // One report every 4 ms (250 Hz) while moving. Before the go file, and after the run, it blocks: a
            // read that never returns is what a tablet nobody touches looks like.
            public byte[] Read()
            {
                while (!_disposed)
                {
                    if (_start == DateTime.MinValue && _goFile != null && File.Exists(_goFile))
                        _start = DateTime.UtcNow;
                    double t = _start == DateTime.MinValue ? -1 : (DateTime.UtcNow - _start).TotalSeconds;
                    if (t >= 0 && t < _seconds)
                    {
                        Thread.Sleep(4);
                        return Report(t);
                    }
                    Thread.Sleep(50);
                }
                throw new IOException("closed");
            }

            // The left-hand tablet draws a circle; the right-hand one a figure of eight. Each presses its tip for
            // 0.3 s every 1.5 s (the right one offset), so a click can be seen too.
            private byte[] Report(double t)
            {
                double u, v;
                if (_t._left) { u = 0.5 + 0.3 * Math.Cos(t * 2.0); v = 0.5 + 0.3 * Math.Sin(t * 2.0); }
                else { u = 0.5 + 0.3 * Math.Sin(t * 1.6); v = 0.5 + 0.25 * Math.Sin(t * 3.2); }
                bool tip = (t + (_t._left ? 0 : 0.75)) % 1.5 < 0.3;
                var r = new byte[_t.InputReportLength];
                r[0] = 0x08;
                Put(r, 2, (int)(u * _t._maxX));
                Put(r, 4, (int)(v * _t._maxY));
                Put(r, 6, tip ? _t._maxPressure / 2 : 0);
                return r;
            }

            private static void Put(byte[] r, int at, int value)
            {
                r[at] = (byte)(value & 0xFF);
                r[at + 1] = (byte)((value >> 8) & 0xFF);
            }

            public void Write(byte[] buffer) { }
            public void GetFeature(byte[] buffer) { }
            public void SetFeature(byte[] buffer) { }
            public void Dispose() => _disposed = true;
        }
    }
}
