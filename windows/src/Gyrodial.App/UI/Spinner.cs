using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Gyrodial.App.UI;

/// <summary>
/// An indeterminate progress ring (WPF has none): a three-quarter arc in the accent colour, turning
/// while it's visible and still while it isn't.
/// </summary>
public sealed class Spinner : FrameworkElement
{
    readonly RotateTransform _spin = new();
    readonly DoubleAnimation _turn = new(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(Brushes.Gold, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    public Spinner()
    {
        RenderTransform = _spin;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged += (_, _) => Animate(IsVisible);
        Unloaded += (_, _) => Animate(false);
    }

    void Animate(bool on) => _spin.BeginAnimation(RotateTransform.AngleProperty, on ? _turn : null);

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double thickness = Math.Max(2, size / 8), r = (size - thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        var pen = new Pen(Stroke, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var start = new Point(c.X, c.Y - r);
        var end = new Point(c.X - r, c.Y);  // 270° clockwise from 12 o'clock
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, isLargeArc: true, SweepDirection.Clockwise, isStroked: true));
        dc.DrawGeometry(null, pen, new PathGeometry([figure]));
    }
}
