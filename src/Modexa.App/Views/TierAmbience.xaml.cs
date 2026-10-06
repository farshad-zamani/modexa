using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Modexa.Core.Licensing;

namespace Modexa.App.Views;

/// <summary>
/// Animated tier ambience behind the pages. Animations are capped at 30 fps, paused while the
/// window is minimized, and disabled entirely when Windows "animation effects" are turned off.
/// </summary>
public partial class TierAmbience : UserControl
{
    private const int Fps = 30;
    private const int EmberCount = 16;

    // Vein curves in a 1000x600 design space (scaled to the page by a shared geometry transform).
    private static readonly string[] VeinData =
    {
        "M -50,430 C 180,360 260,530 470,455 S 760,300 1050,360",
        "M -50,110 C 160,180 300,50 520,140 S 820,260 1050,170",
        "M 190,660 C 260,520 420,560 520,465 S 700,330 760,-50",
        "M 1050,560 C 900,500 820,630 660,585 S 420,420 300,-50",
        "M 470,455 C 500,520 560,540 620,610",
        "M 520,140 C 560,90 640,80 700,-30",
    };

    private readonly ScaleTransform _scale = new(1, 1);
    private readonly Path[] _veins;
    private readonly Path[] _pulses;
    private readonly List<Storyboard> _ambient = new();   // tier-wide loops
    private readonly List<Storyboard> _pulseRuns = new(); // depend on page size, restarted on resize
    private readonly List<(Ellipse dot, TranslateTransform tx)> _embers = new();
    private readonly Random _rng = new(7);
    private readonly DispatcherTimer _resizeDebounce;
    private LicenseTier _tier = LicenseTier.Free;
    private Window? _host;

    public TierAmbience()
    {
        InitializeComponent();
        _veins = new[] { Vein1, Vein2, Vein3, Vein4, Vein5, Vein6 };
        _pulses = new[] { Pulse1, Pulse2, Pulse3 };

        for (int i = 0; i < _veins.Length; i++)
        {
            _veins[i].Data = Scaled(VeinData[i]);
            _veins[i].SetResourceReference(Shape.StrokeProperty, "Gold");
        }
        for (int i = 0; i < _pulses.Length; i++)
        {
            _pulses[i].Data = Scaled(VeinData[i]);
            _pulses[i].SetResourceReference(Shape.StrokeProperty, "Accent");
        }

        _resizeDebounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _resizeDebounce.Tick += (_, _) => { _resizeDebounce.Stop(); RestartPulses(); };

        SizeChanged += (_, _) => { Layout(); if (_tier == LicenseTier.Pro) { _resizeDebounce.Stop(); _resizeDebounce.Start(); } };
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
        StopAll(_pulseRuns);

        Root.Visibility = tier == LicenseTier.Free ? Visibility.Collapsed : Visibility.Visible;
        if (tier == LicenseTier.Free) return;

        bool pro = tier == LicenseTier.Pro;
        VeinLayer.Opacity = pro ? 0.16 : 0.10;
        PulseLayer.Opacity = pro ? 0.9 : 0;
        AuroraLayer.Opacity = pro ? 0.16 : 0;
        EmberLayer.Opacity = pro ? 1 : 0;

        if (pro) EnsureEmbers();
        Layout();
        if (!AnimationsAllowed) return;

        // Veins breathe on both paid tiers.
        Run(_ambient, Animate(VeinLayer, OpacityProperty, pro ? 0.10 : 0.06, pro ? 0.22 : 0.14, 7, autoReverse: true));
        if (!pro) return;

        // Aurora drifts slowly, embers rise, pulses run along the veins.
        Run(_ambient, Animate(AuroraATx, TranslateTransform.XProperty, -140, 160, 19, autoReverse: true));
        Run(_ambient, Animate(AuroraBTx, TranslateTransform.XProperty, 120, -180, 23, autoReverse: true));
        Run(_ambient, Animate(AuroraLayer, OpacityProperty, 0.10, 0.20, 9, autoReverse: true));
        StartEmbers();
        RestartPulses();
    }

    /// <summary>One bright dash per vein; the dash period is derived from the vein's real length.</summary>
    private void RestartPulses()
    {
        StopAll(_pulseRuns);
        if (_tier != LicenseTier.Pro || !AnimationsAllowed || ActualWidth <= 0) return;

        double[] seconds = { 4.5, 5.5, 5.0 };
        double[] delays = { 0, 1.8, 3.4 };
        for (int i = 0; i < _pulses.Length; i++)
        {
            var p = _pulses[i];
            double t = p.StrokeThickness;
            double lengthUnits = PathLength(p.Data) / t;   // dash units are multiples of thickness
            double dash = 22 / t;
            double gap = lengthUnits * 1.8;                // one dash on the path at a time, then a rest
            p.StrokeDashArray = new DoubleCollection { dash, gap };
            Run(_pulseRuns, Animate(p, Shape.StrokeDashOffsetProperty, dash, -(lengthUnits + dash * 2),
                seconds[i] * 2.0, autoReverse: false, beginSeconds: delays[i]));
        }
    }

    private static double PathLength(Geometry g)
    {
        double len = 0;
        foreach (var fig in g.GetFlattenedPathGeometry(0.5, ToleranceType.Absolute).Figures)
        {
            Point prev = fig.StartPoint;
            foreach (var seg in fig.Segments)
            {
                if (seg is PolyLineSegment pl)
                    foreach (var pt in pl.Points) { len += (pt - prev).Length; prev = pt; }
                else if (seg is LineSegment ls) { len += (ls.Point - prev).Length; prev = ls.Point; }
            }
        }
        return Math.Max(len, 1);
    }

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

            var sb = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(_rng.NextDouble() * dur) };
            Timeline.SetDesiredFrameRate(sb, Fps);
            var move = new DoubleAnimation(0, rise, TimeSpan.FromSeconds(dur));
            Storyboard.SetTarget(move, tx);
            Storyboard.SetTargetProperty(move, new PropertyPath(TranslateTransform.YProperty));
            var fade = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromSeconds(dur) };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.75, KeyTime.FromPercent(0.2)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.4, KeyTime.FromPercent(0.7)));
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

    private static Storyboard Animate(DependencyObject target, DependencyProperty prop, double from, double to,
        double seconds, bool autoReverse, double beginSeconds = 0)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = autoReverse,
            EasingFunction = autoReverse ? new SineEase { EasingMode = EasingMode.EaseInOut } : null
        };
        Storyboard.SetTarget(anim, target);
        Storyboard.SetTargetProperty(anim, new PropertyPath(prop));
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
        foreach (var sb in _ambient.Concat(_pulseRuns))
        {
            if (minimized) sb.Pause(this);
            else sb.Resume(this);
        }
    }
}
