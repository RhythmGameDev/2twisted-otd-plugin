using System;
using System.Numerics;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;

namespace TwoTwisted.OtdPlugin
{
    // OUTPUT MODES THAT GIVE EACH TABLET ITS OWN CURSOR IN 2TWISTED.
    //
    // OpenTabletDriver's own Absolute and Relative modes move the Windows cursor, and Windows has one cursor: two
    // pens on two tablets end up fighting over it, and the game cannot tell them apart (injected input carries no
    // device). These modes move nothing on the desktop. Each writes its tablet's pen into its own slot of a block of
    // memory the game also maps (PenBoard), and the game gives every tablet a device of its own: one pen per hand.
    //
    // HOW TO USE: set the Output Mode of EACH tablet to one of
    //   "2Twisted (one cursor per tablet)": absolute, like Absolute Mode. The tablet area is the part of the tablet
    //     you use, and the display area's shape is fitted to the play area (a display area the shape of your screen
    //     behaves exactly as Absolute Mode does with the game).
    //   "2Twisted relative (one cursor per tablet)": like Relative Mode, the pen pushes its cursor as a mouse does;
    //     the speed is the profile's relative sensitivity, then the game's mouse sensitivity for that hand.
    // The pen tip, and any pen button bound to a mouse button, is that hand's button. Nothing reaches the desktop,
    // so switch back to Absolute or Relative Mode to use the pen anywhere else.
    //
    // OpenTabletDriver makes one output mode per tablet, and each writes under its tablet's name, so two tablets of
    // different models are two devices. (Two of the same model are one tablet to OpenTabletDriver 0.6, which only
    // detects the first: that needs a change to the driver itself.)
    [PluginName(PluginTitle)]
    public class TwoTwistedOutputMode : AbsoluteOutputMode, IDisposable
    {
        public const string PluginTitle = "2Twisted (one cursor per tablet)";

        private readonly AbsolutePenPointer _pointer;

        public TwoTwistedOutputMode()
        {
            _pointer = new AbsolutePenPointer(this);
        }

        // Not [Resolved]: the pointer is this mode's own, not the shared desktop one (OpenTabletDriver hands every
        // tablet the same desktop pointer, which is the whole problem this plugin exists to get around).
        public override IAbsolutePointer Pointer
        {
            get => _pointer;
            set { }
        }

        protected override void OnOutput(IDeviceReport report)
        {
            base.OnOutput(report);   // the transformed position reaches the pointer's SetPosition here
            _pointer.Channel.UpdatePressure(report, Tablet);
            if (PenReport.IsPen(report, out bool inRange)) _pointer.Publish(inRange);
        }

        // Explicit, so it also answers IDisposable on 0.6.8, whose OutputMode has a Dispose of its own that this
        // build (against 0.6.0.4) cannot see: re-implementing the interface here is what makes this one called.
        // Called when settings are applied or the tablet goes away (earlier 0.6 releases never dispose an output
        // mode). Tells the game this tablet's cursor is gone.
        void IDisposable.Dispose() => _pointer.Publish(inRange: false, goodbye: true);

        // The display area as the game should fit it: its rectangle in desktop pixels.
        internal (Vector2 Min, Vector2 Size) DisplayArea()
        {
            Area? o = Output;
            if (o == null || o.Width <= 0 || o.Height <= 0) return (Vector2.Zero, Vector2.One);
            var size = new Vector2(o.Width, o.Height);
            return (o.Position - size / 2, size);
        }
    }

    [PluginName(PluginTitle)]
    public class TwoTwistedRelativeMode : RelativeOutputMode, IDisposable
    {
        public const string PluginTitle = "2Twisted relative (one cursor per tablet)";

        private readonly RelativePenPointer _pointer;

        public TwoTwistedRelativeMode()
        {
            _pointer = new RelativePenPointer(this);
        }

        public override IRelativePointer Pointer
        {
            get => _pointer;
            set { }
        }

        protected override void OnOutput(IDeviceReport report)
        {
            base.OnOutput(report);   // the movement since the last report reaches the pointer's SetPosition here
            _pointer.Channel.UpdatePressure(report, Tablet);
            if (PenReport.IsPen(report, out bool inRange)) _pointer.Publish(inRange);
        }

        void IDisposable.Dispose() => _pointer.Publish(inRange: false, goodbye: true);
    }

    internal static class PenReport
    {
        // A pen report is "in range"; the driver's out-of-range report (0.6.5 and later; the type is named rather
        // than referenced so this builds against 0.6.0.4) says the pen has gone. Anything else (the tablet's own
        // buttons) says nothing about the pen.
        public static bool IsPen(IDeviceReport report, out bool inRange)
        {
            inRange = report is IAbsolutePositionReport;
            return inRange || report.GetType().Name == "OutOfRangeReport";
        }
    }
}
