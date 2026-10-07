using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Modexa.Core.Licensing;

namespace Modexa.App.Views;

/// <summary>
/// Animated tier ambience behind the pages: gold veins with beads of light gliding back and forth
/// along them (subtle on Plus, rich on Pro), plus an aurora and rising embers on Pro. Animations are
/// capped at 30 fps, paused while the window is minimized, and disabled when Windows "animation
/// effects" are turned off (the static veins still show).
/// </summary>
public partial class TierAmbience : UserControl
{
    private const int Fps = 30;
    private const int EmberCount = 18;

    // Vein curves in a 1000x600 design space (scaled to the page by a shared geometry transform).
    private static readonly string[] VeinData =
    {
        "M -60,430 C 180,360 260,530 470,455 S 760,300 1060,360",
        "M -60,110 C 160,180 300,50 520,140 S 820,260 1060,170",
        "M 190,660 C 260,520 420,560 520,465 S 700,330 760,-60",
        "M 1060,560 C 900,500 820,630 660,585 S 420,420 300,-60",
        "M -60,560 C 140,520 240,610 400,560 S 640,470 760,660",
        "M 470,455 C 500,520 560,540 620,660",
        "M 520,140 C 560,90 640,80 700,-60",
        "M 1060,250 C 930,300 880,220 780,260 S 640,380 600,300",
    };

    // Per vein: thickness (design units are pixels — the pen is not scaled).
    private static readonly double[] VeinThickness = { 1.4, 1.2, 1.1, 1.0, 0.9, 0.8, 0.8, 0.8 };

    // Beads: (vein index, seconds per leg, start delay, size). Plus uses the first three.
    private static readonly (int vein, double seconds, double delay, double size)[] BeadPlan =
    {
        (0, 13.0, 0.0, 7.0),
        (1, 15.0, 2.5, 6.5),
        (3, 14.0, 5.0, 6.5),
        (2, 11.0, 1.2, 8.0),
        (4, 12.5, 3.6, 7.0),
        (0, 9.5, 6.4, 9.0),
        (7, 10.0, 4.4, 7.0),
        (1, 10.5, 8.0, 7.5),
    };

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Path[] _veins;
    private readonly Path[] _glows;
    private readonly List<Storyboard> _ambient = new();    // tier-wide loops
    private readonly List<Storyboard> _beadRuns = new();   // depend on page size, restarted on resize
    private readonly List<(FrameworkElement bead, TranslateTransform tx)> _beads = new();
    private readonly List<(Ellipse dot, TranslateTransform tx)> _embers = new();
    private readonly Random _rng = new(7);
    private readonly DispatcherTimer _resizeDebounce;
    private LicenseTier _tier = LicenseTier.Free;
    private Window? _host;

    public TierAmbience()
    {
        InitializeComponent();

        _veins = new Path[VeinData.Length];
        _glows = new Path[VeinData.Length];
        for (int i = 0; i < VeinData.Length; i++)
        {
            _glows[i] = new Path { Data = Scaled(VeinData[i]), StrokeThickness = VeinThickness[i] * 6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            _veins[i] = new Path { Data = Scaled(VeinData[i]), StrokeThickness = VeinThickness[i] };
            GlowLayer.Children.Add(_glows[i]);
            VeinLayer.Children.Add(_veins[i]);
        }

        _resizeDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); RestartBeads(); };

        SizeChanged += (_, _) =>
        {
            Layout();
            if (_tier != LicenseTier.Free) { _resizeDebounce.Stop(); _resizeDebounce.Start(); }
        };
        Loaded += (_, _) =>
        {
            _host = Window.GetWindow(this);
            if (_host != null) _host.StateChanged += Host_StateChanged;
        };
        Unloaded += (_, _) => { if (_host != null) _host.StateChanged -= Host_StateChanged; };
    }

    private Geometry Scaled(string data)
    {
        var g = Geometry.Parse(data).Clone();
        g.Transform = _scale; // geometry transform: shape scales, pen width does not
        return g;
    }

    private static bool AnimationsAllowed => SystemParameters.ClientAreaAnimation;

    public void SetTier(LicenseTier tier)
    {
        _tier = tier;
        StopAll(_ambient);
        StopAll(_beadRuns);

        Root.Visibility = tier == LicenseTier.Free ? Visibility.Collapsed : Visibility.Visible;
        if (tier == LicenseTier.Free) return;

        bool pro = tier == LicenseTier.Pro;
        Color gold = ThemeColor("C.Glow", Color.FromRgb(0xDB, 0xA9, 0x4C));
        ApplyVeinBrushes(gold, pro);

        VeinLayer.Opacity = pro ? 0.55 : 0.34;
        GlowLayer.Opacity = pro ? 0.10 : 0;
        BeadLayer.Opacity = 1;
        AuroraLayer.Opacity = pro ? 0.16 : 0;
        EmberLayer.Opacity = pro ? 1 : 0;

        BuildBeads(gold, pro);
        if (pro) EnsureEmbers();
        Layout();
        if (!AnimationsAllowed)
        {
            BeadLayer.Opacity = 0; // static beads would just look like specks
            return;
        }

        // Veins breathe on both paid tiers (Pro deeper and brighter).
        Run(_ambient, Animate(VeinLayer, OpacityProperty, pro ? 0.42 : 0.24, pro ? 0.68 : 0.40, 7, autoReverse: true));
        if (pro)
        {
            Run(_ambient, Animate(GlowLayer, OpacityProperty, 0.06, 0.16, 7, autoReverse: true));
            Run(_ambient, Animate(AuroraA, TranslateTransform.XProperty, -140, 160, 19, autoReverse: true, path: "(UIElement.RenderTransform).(TranslateTransform.X)"));
            Run(_ambient, Animate(AuroraB, TranslateTransform.XProperty, 120, -180, 23, autoReverse: true, path: "(UIElement.RenderTransform).(TranslateTransform.X)"));
            Run(_ambient, Animate(AuroraLayer, OpacityProperty, 0.10, 0.22, 9, autoReverse: true));
            StartEmbers();
        }
        RestartBeads();
    }

    private Color ThemeColor(string key, Color fallback)
        => TryFindResource(key) is Color c ? c : fallback;

    /// <summary>Veins fade in and out at their ends so they read as threads of light, not wires.</summary>
    private void ApplyVeinBrushes(Color gold, bool pro)
    {
        for (int i = 0; i < _veins.Length; i++)
        {
            bool horizontal = i is 0 or 1 or 4 or 7;
            var brush = new LinearGradientBrush
            {
                StartPoint = horizontal ? new Point(0, 0.5) : new Point(0.5, 0),
                EndPoint = horizontal ? new Point(1, 0.5) : new Point(0.5, 1)
            };
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, gold.R, gold.G, gold.B), 0));
            brush.GradientStops.Add(new GradientStop(gold, 0.22));
            brush.GradientStops.Add(new GradientStop(pro ? Color.FromRgb(0xFF, 0xEC, 0xB0) : gold, 0.5));
            brush.GradientStops.Add(new GradientStop(gold, 0.78));
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, gold.R, gold.G, gold.B), 1));
            brush.Freeze();
            _veins[i].Stroke = brush;
            _glows[i].Stroke = brush;
            // Plus shows fewer threads: the minor veins stay hidden.
            bool visible = pro || i < 5;
            _veins[i].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            _glows[i].Visibility = pro ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- beads -------------------------------------------------------------------------------

    private void BuildBeads(Color gold, bool pro)
    {
        BeadLayer.Children.Clear();
        _beads.Clear();
        int count = pro ? BeadPlan.Length : 3;
        for (int i = 0; i < count; i++)
        {
            double size = BeadPlan[i].size * (pro ? 1.0 : 0.85);
            double halo = size * (pro ? 6.5 : 4.5);

            var grid = new Grid { Width = halo, Height = halo, Opacity = pro ? 1 : 0.8 };
            var haloBrush = new RadialGradientBrush();
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(pro ? (byte)0x9C : (byte)0x60, gold.R, gold.G, gold.B), 0));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(pro ? (byte)0x2C : (byte)0x1A, gold.R, gold.G, gold.B), 0.4));
            haloBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, gold.R, gold.G, gold.B), 1));
            haloBrush.Freeze();
            grid.Children.Add(new Ellipse { Fill = haloBrush });

            var coreBrush = new RadialGradientBrush();
            coreBrush.GradientStops.Add(new GradientStop(Color.FromRgb(0xFF, 0xF8, 0xE1), 0));
            coreBrush.GradientStops.Add(new GradientStop(gold, 0.6));
            coreBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, gold.R, gold.G, gold.B), 1));
            coreBrush.Freeze();
            grid.Children.Add(new Ellipse { Width = size, Height = size, Fill = coreBrush, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });

            var tx = new TranslateTransform(-1000, -1000); // parked off-screen until animated
            grid.RenderTransform = tx;
            Canvas.SetLeft(grid, -halo / 2);
            Canvas.SetTop(grid, -halo / 2);
            BeadLayer.Children.Add(grid);
            _beads.Add((grid, tx));
        }
    }

    /// <summary>Beads glide along their vein to the far end and back (path depends on page size).</summary>
    private void RestartBeads()
    {
        StopAll(_beadRuns);
        if (_tier == LicenseTier.Free || !AnimationsAllowed || ActualWidth <= 0 || ActualHeight <= 0) return;

        for (int i = 0; i < _beads.Count; i++)
        {
            var (vein, seconds, delay, _) = BeadPlan[i];
            // Flattening bakes the current scale into the path the animation follows.
            var path = _veins[vein].Data.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute);
            path.Freeze();
            var (bead, _) = _beads[i];

            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delay) };
            Timeline.SetDesiredFrameRate(sb, Fps);
            foreach (var (source, prop) in new[] { (PathAnimationSource.X, "X"), (PathAnimationSource.Y, "Y") })
            {
                var a = new DoubleAnimationUsingPath
                {
                    PathGeometry = path,
                    Source = source,
                    Duration = TimeSpan.FromSeconds(seconds),
                    AutoReverse = true,
                    AccelerationRatio = 0.25,
                    DecelerationRatio = 0.25
                };
                // Target the element + property path (a bare Freezable target is not animated reliably).
                Storyboard.SetTarget(a, bead);
                Storyboard.SetTargetProperty(a, new PropertyPath($"(UIElement.RenderTransform).(TranslateTransform.{prop})"));
                sb.Children.Add(a);
            }
            Run(_beadRuns, sb);
        }
    }

    // ---- embers ------------------------------------------------------------------------------

    private void EnsureEmbers()
    {
        if (_embers.Count > 0) return;
        for (int i = 0; i < EmberCount; i++)
        {
            double size = 2 + _rng.NextDouble() * 2.5;
            var tx = new TranslateTransform();
            var dot = new Ellipse { Width = size, Height = size, Opacity = 0, RenderTransform = tx };
            dot.SetResourceReference(Shape.FillProperty, "Gold");
            EmberLayer.Children.Add(dot);
            _embers.Add((dot, tx));
        }
    }

    private void StartEmbers()
    {
        foreach (var (dot, tx) in _embers)
        {
            double dur = 7 + _rng.NextDouble() * 6;
            double rise = -(ActualHeight > 0 ? ActualHeight : 700) * (0.45 + _rng.NextDouble() * 0.4);

            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(_rng.NextDouble() * 3) };
            Timeline.SetDesiredFrameRate(sb, Fps);
            var move = new DoubleAnimation(0, rise, TimeSpan.FromSeconds(dur));
            Storyboard.SetTarget(move, dot);
            Storyboard.SetTargetProperty(move, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(dur) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.8, KeyTime.FromPercent(0.2)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.45, KeyTime.FromPercent(0.7)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            Storyboard.SetTarget(fade, dot);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            sb.Children.Add(move);
            sb.Children.Add(fade);
            Run(_ambient, sb);
        }
    }

    private void Layout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        _scale.ScaleX = w / 1000.0;
        _scale.ScaleY = h / 600.0;
        Canvas.SetLeft(AuroraB, w - 700);
        for (int i = 0; i < _embers.Count; i++)
        {
            Canvas.SetLeft(_embers[i].dot, w * ((i + 0.5) / _embers.Count) + (_rng.NextDouble() - 0.5) * 60);
            Canvas.SetTop(_embers[i].dot, h + 6);
        }
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static Storyboard Animate(DependencyObject target, DependencyProperty prop, double from, double to,
        double seconds, bool autoReverse, double beginSeconds = 0, string? path = null)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = autoReverse,
            EasingFunction = autoReverse ? new SineEase { EasingMode = EasingMode.EaseInOut } : null
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, path != null ? new PropertyPath(path) : new PropertyPath(prop));
        var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(beginSeconds) };
        Timeline.SetDesiredFrameRate(sb, Fps);
        sb.Children.Add(anim);
        return sb;
    }

    private void Run(List<Storyboard> bucket, Storyboard sb)
    {
        sb.Begin(this, isControllable: true);
        bucket.Add(sb);
        if (_host?.WindowState == WindowState.Minimized) sb.Pause(this);
    }

    private void StopAll(List<Storyboard> bucket)
    {
        foreach (var sb in bucket) sb.Stop(this);
        bucket.Clear();
    }

    private void Host_StateChanged(object? sender, EventArgs e)
    {
        bool minimized = _host?.WindowState == WindowState.Minimized;
        foreach (var sb in _ambient.Concat(_beadRuns))
        {
            if (minimized) sb.Pause(this);
            else sb.Resume(this);
        }
    }
}
