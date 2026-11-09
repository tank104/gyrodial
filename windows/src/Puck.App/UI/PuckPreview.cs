using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Puck.App.UI;

/// <summary>
/// A small drawing of the puck: the turned-aluminium rim, the black glass, the volume as a yellow arc
/// round the edge and the current face's name in the middle.
/// </summary>
public sealed class PuckPreview : FrameworkElement
{
    public static readonly DependencyProperty VolumeProperty = Register(nameof(Volume), -1);
    public static readonly DependencyProperty MutedProperty = Register(nameof(Muted), false);
    public static readonly DependencyProperty FaceProperty = Register(nameof(Face), "");
    public static readonly DependencyProperty IsConnectedProperty = Register(nameof(IsConnected), false);

    static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(PuckPreview),
            new FrameworkPropertyMetadata(fallback, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Volume { get => (int)GetValue(VolumeProperty); set => SetValue(VolumeProperty, value); }
    public bool Muted { get => (bool)GetValue(MutedProperty); set => SetValue(MutedProperty, value); }
    public string Face { get => (string)GetValue(FaceProperty); set => SetValue(FaceProperty, value); }
    public bool IsConnected { get => (bool)GetValue(IsConnectedProperty); set => SetValue(IsConnectedProperty, value); }

    static readonly Brush Yellow = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
    static readonly Brush Glass = Freeze(new SolidColorBrush(Color.FromRgb(0x05, 0x05, 0x06)));
    static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));
    static readonly Brush White = Freeze(new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF7)));
    static readonly Brush Grey = Freeze(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
    static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    static readonly Brush Rim = Freeze(new LinearGradientBrush(
        [new GradientStop(Color.FromRgb(0x9A, 0x9A, 0x9F), 0), new GradientStop(Color.FromRgb(0x4A, 0x4A, 0x4E), 0.5), new GradientStop(Color.FromRgb(0x7A, 0x7A, 0x80), 1)],
        new Point(0, 0), new Point(1, 1)));
    static readonly Pen RimEdge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0))), 1));

    static T Freeze<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }

    protected override Size MeasureOverride(Size available)
    {
        double s = double.IsInfinity(available.Width) ? 120 : Math.Min(available.Width, double.IsInfinity(available.Height) ? available.Width : available.Height);
        return new Size(s, s);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2;
        double glassR = r * 0.86;

        dc.DrawEllipse(Rim, RimEdge, c, r - 0.5, r - 0.5);
        dc.DrawEllipse(Glass, null, c, glassR, glassR);

        // the volume ring, 300° from 7 o'clock, like a knob's travel
        double ringR = glassR * 0.86, thick = Math.Max(2, size * 0.035);
        const double start = 120, sweep = 300;
        DrawArc(dc, c, ringR, start, sweep, new Pen(Track, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        if (IsConnected && Volume >= 0)
        {
            double v = Math.Clamp(Volume / 100.0, 0, 1);
            if (v > 0.005)
                DrawArc(dc, c, ringR, start, sweep * v, new Pen(Muted ? Grey : Yellow, thick) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var face = new Typeface(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        if (!IsConnected)
        {
            // asleep: two closed eyes
            var lid = new Pen(Grey, Math.Max(2, size * 0.035)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            double ex = glassR * 0.28, ey = c.Y;
            dc.DrawLine(lid, new Point(c.X - ex - size * 0.06, ey), new Point(c.X - ex + size * 0.06, ey));
            dc.DrawLine(lid, new Point(c.X + ex - size * 0.06, ey), new Point(c.X + ex + size * 0.06, ey));
            return;
        }

        string big = Muted ? "—" : Volume >= 0 ? Volume.ToString(CultureInfo.InvariantCulture) : "";
        var num = new FormattedText(big, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size * 0.27, Muted ? Red : White, dpi);
        dc.DrawText(num, new Point(c.X - num.Width / 2, c.Y - num.Height * 0.62));

        var label = AppModel.FriendlyFace(Face ?? "").ToUpperInvariant();
        var small = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, Math.Max(7, size * 0.085), Grey, dpi);
        dc.DrawText(small, new Point(c.X - small.Width / 2, c.Y + num.Height * 0.36));
    }

    static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        if (sweepDeg <= 0) return;
        static Point At(Point c, double r, double deg)
        {
            double a = deg * Math.PI / 180;
            return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
        }
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(At(c, r, startDeg), false, false);
            g.ArcTo(At(c, r, startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        geo.Freeze();
        dc.DrawGeometry(null, pen, geo);
    }
}

/// <summary>A small battery: outline, fill to the level (red when low, green while charging).</summary>
public sealed class BatteryGauge : FrameworkElement
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(nameof(Level), typeof(int), typeof(BatteryGauge),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(BatteryGauge),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(BatteryGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public int Level { get => (int)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(22, 12);

    protected override void OnRender(DrawingContext dc)
    {
        if (Level < 0) return;
        var outline = new Pen(Foreground, 1.2);
        var body = new Rect(0.6, 0.6, 18.5, 10.8);
        dc.DrawRoundedRectangle(null, outline, body, 2.5, 2.5);
        dc.DrawRoundedRectangle(Foreground, null, new Rect(19.8, 3.8, 1.8, 4.4), 0.8, 0.8);
        double level = Math.Clamp(Level / 100.0, 0, 1);
        Brush fill = Level < 15 && !Charging ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))
            : Charging ? new SolidColorBrush(Color.FromRgb(0x5A, 0xD2, 0x6A)) : Foreground;
        if (level > 0) dc.DrawRoundedRectangle(fill, null, new Rect(2.4, 2.4, Math.Max(1.5, 14.9 * level), 7.2), 1.2, 1.2);
    }
}
