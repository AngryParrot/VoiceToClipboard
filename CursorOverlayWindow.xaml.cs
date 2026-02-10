using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace VoiceToClipboard;

public partial class CursorOverlayWindow : Window
{
    #region P/Invoke

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    #endregion

    private const int CursorOffsetX = 2;
    private const int CursorOffsetY = -20;
    private const double LerpFactor = 0.35;

    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(239, 68, 68));
    private static readonly Color RedGlowColor = Color.FromRgb(239, 68, 68);

    private readonly DispatcherTimer _trackingTimer;
    private readonly DispatcherTimer _doneHoldTimer;

    private double _targetLeft, _targetTop;
    private bool _firstPosition;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;
    private bool _dpiInitialized;
    private int _logThrottleCounter;

    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "vtc.log");

    public CursorOverlayWindow()
    {
        InitializeComponent();

        _trackingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _trackingTimer.Tick += TrackingTimer_Tick;

        _doneHoldTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _doneHoldTimer.Tick += DoneHoldTimer_Tick;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT);
    }

    private void EnsureDpiInitialized()
    {
        if (_dpiInitialized) return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            _dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
            _dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            _dpiInitialized = true;
            Log($"[Overlay] DPI scale: {_dpiScaleX:F2}x{_dpiScaleY:F2}");
        }
    }

    #region Public API

    public void ShowRecording()
    {
        Log("[Overlay] ShowRecording");
        StopAllAnimations();
        ResetBaseValues();

        Dot.Fill = RedBrush;
        DotGlow.Color = RedGlowColor;
        DotGlow.BlurRadius = 6;
        Dot.Visibility = Visibility.Visible;
        PenIcon.Visibility = Visibility.Collapsed;
        Checkmark.Visibility = Visibility.Collapsed;

        _firstPosition = true;
        PositionAtCursorImmediate();
        Show();
        EnsureDpiInitialized();
        _logThrottleCounter = 0;
        _trackingTimer.Start();

        AnimateAppear(() => StartRecordingPulse());
    }

    public void ShowTranscribing()
    {
        Log("[Overlay] ShowTranscribing");
        _doneHoldTimer.Stop();
        StopRecordingPulse();

        if (!IsVisible)
        {
            StopAllAnimations();
            ResetBaseValues();

            PenIcon.Visibility = Visibility.Visible;
            Dot.Visibility = Visibility.Collapsed;
            Checkmark.Visibility = Visibility.Collapsed;

            _firstPosition = true;
            PositionAtCursorImmediate();
            Show();
            EnsureDpiInitialized();
            _trackingTimer.Start();
            AnimateAppear();
            return;
        }

        AnimateSquishTransition(() =>
        {
            Dot.Visibility = Visibility.Collapsed;
            PenIcon.Visibility = Visibility.Visible;
            Checkmark.Visibility = Visibility.Collapsed;
        });
    }

    public void ShowDone()
    {
        Log("[Overlay] ShowDone");
        StopAllAnimations();
        ResetBaseValues();

        Dot.Visibility = Visibility.Collapsed;
        PenIcon.Visibility = Visibility.Collapsed;
        Checkmark.Visibility = Visibility.Visible;

        if (!IsVisible)
        {
            _firstPosition = true;
            PositionAtCursorImmediate();
            Show();
            EnsureDpiInitialized();
            _trackingTimer.Start();
        }

        AnimateCheckBounce();
        _doneHoldTimer.Start();
    }

    public void HideOverlay()
    {
        Log("[Overlay] HideOverlay");
        _doneHoldTimer.Stop();
        _trackingTimer.Stop();
        StopAllAnimations();
        ResetBaseValues();

        Dot.Visibility = Visibility.Collapsed;
        PenIcon.Visibility = Visibility.Collapsed;
        Checkmark.Visibility = Visibility.Collapsed;

        if (IsVisible) Hide();
    }

    #endregion

    #region Animations

    private void AnimateAppear(Action? onCompleted = null)
    {
        // Clear any existing container animations
        Container.BeginAnimation(OpacityProperty, null);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        Container.Opacity = 0;
        ContainerScale.ScaleX = 0.5;
        ContainerScale.ScaleY = 0.5;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(200);

        var opacityAnim = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        opacityAnim.Completed += (_, _) =>
        {
            Container.BeginAnimation(OpacityProperty, null);
            Container.Opacity = 1;
            onCompleted?.Invoke();
        };

        var scaleXAnim = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        scaleXAnim.Completed += (_, _) =>
        {
            ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ContainerScale.ScaleX = 1;
        };

        var scaleYAnim = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        scaleYAnim.Completed += (_, _) =>
        {
            ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ContainerScale.ScaleY = 1;
        };

        Container.BeginAnimation(OpacityProperty, opacityAnim);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
    }

    private void StartRecordingPulse()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        var duration = TimeSpan.FromMilliseconds(900);

        Dot.BeginAnimation(OpacityProperty, new DoubleAnimation(0.65, 1.0, duration)
        {
            EasingFunction = ease,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });

        DotScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, 1.12, duration)
        {
            EasingFunction = ease,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });

        DotScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, 1.12, duration)
        {
            EasingFunction = ease,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });

        DotGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, new DoubleAnimation(6, 14, duration)
        {
            EasingFunction = ease,
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void StopRecordingPulse()
    {
        Dot.BeginAnimation(OpacityProperty, null);
        Dot.Opacity = 1;
        DotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DotScale.ScaleX = 1;
        DotScale.ScaleY = 1;
        DotGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        DotGlow.BlurRadius = 6;
    }

    private void AnimateSquishTransition(Action midpointAction)
    {
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var squishDuration = TimeSpan.FromMilliseconds(150);

        var squishX = new DoubleAnimation(0.85, squishDuration) { EasingFunction = easeIn };
        var squishY = new DoubleAnimation(0.85, squishDuration) { EasingFunction = easeIn };

        squishX.Completed += (_, _) =>
        {
            // Swap visuals at the midpoint
            midpointAction();

            // Set base to squished value, then bounce back
            ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ContainerScale.ScaleX = 0.85;
            ContainerScale.ScaleY = 0.85;

            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

            var bounceX = new DoubleAnimation(1.0, squishDuration) { EasingFunction = easeOut };
            bounceX.Completed += (_, _) =>
            {
                ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                ContainerScale.ScaleX = 1;
            };

            var bounceY = new DoubleAnimation(1.0, squishDuration) { EasingFunction = easeOut };
            bounceY.Completed += (_, _) =>
            {
                ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                ContainerScale.ScaleY = 1;
            };

            ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, bounceX);
            ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, bounceY);
        };

        ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, squishX);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, squishY);
    }

    private void AnimateCheckBounce()
    {
        CheckScale.ScaleX = 0;
        CheckScale.ScaleY = 0;

        var ease = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(350);

        var scaleXAnim = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        scaleXAnim.Completed += (_, _) =>
        {
            CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            CheckScale.ScaleX = 1;
        };

        var scaleYAnim = new DoubleAnimation(1.0, duration) { EasingFunction = ease };
        scaleYAnim.Completed += (_, _) =>
        {
            CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            CheckScale.ScaleY = 1;
        };

        CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);
    }

    private void AnimateDoneFadeOut()
    {
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        var duration = TimeSpan.FromMilliseconds(400);

        var opacityAnim = new DoubleAnimation(0, duration) { EasingFunction = ease };
        opacityAnim.Completed += (_, _) =>
        {
            _trackingTimer.Stop();
            StopAllAnimations();
            ResetBaseValues();
            Dot.Visibility = Visibility.Collapsed;
            PenIcon.Visibility = Visibility.Collapsed;
            Checkmark.Visibility = Visibility.Collapsed;
            Hide();
            Log("[Overlay] Done fade-out completed");
        };

        Container.BeginAnimation(OpacityProperty, opacityAnim);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.7, duration) { EasingFunction = ease });
        ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.7, duration) { EasingFunction = ease });
    }

    private void StopAllAnimations()
    {
        _doneHoldTimer.Stop();

        Dot.BeginAnimation(OpacityProperty, null);
        DotScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DotScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        DotGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        Container.BeginAnimation(OpacityProperty, null);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ContainerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CheckScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CheckScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        PenScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        PenScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    private void ResetBaseValues()
    {
        Container.Opacity = 1;
        ContainerScale.ScaleX = 1;
        ContainerScale.ScaleY = 1;
        Dot.Opacity = 1;
        DotScale.ScaleX = 1;
        DotScale.ScaleY = 1;
        DotGlow.BlurRadius = 6;
        CheckScale.ScaleX = 1;
        CheckScale.ScaleY = 1;
        PenScale.ScaleX = 1;
        PenScale.ScaleY = 1;
    }

    #endregion

    #region Mouse Tracking

    private void TrackingTimer_Tick(object? sender, EventArgs e)
    {
        UpdateTargetFromCursor();

        if (_firstPosition)
        {
            Left = _targetLeft;
            Top = _targetTop;
            _firstPosition = false;
        }
        else
        {
            Left += (_targetLeft - Left) * LerpFactor;
            Top += (_targetTop - Top) * LerpFactor;
        }

        _logThrottleCounter++;
        if (_logThrottleCounter % 33 == 0)
        {
            Log($"[Overlay] Pos: {Left:F0},{Top:F0}");
        }
    }

    private void UpdateTargetFromCursor()
    {
        if (!GetCursorPos(out var pt)) return;

        double cursorX = pt.X;
        double cursorY = pt.Y;

        // Convert to WPF DIPs
        if (_dpiInitialized)
        {
            cursorX /= _dpiScaleX;
            cursorY /= _dpiScaleY;
        }

        // Screen bounds in DIPs
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        // Flip offset direction near screen edges
        double offsetX = (cursorX + CursorOffsetX + Width > screenRight)
            ? -CursorOffsetX - Width
            : CursorOffsetX;
        double offsetY = (cursorY + CursorOffsetY < screenTop)
            ? -CursorOffsetY
            : CursorOffsetY;

        _targetLeft = Math.Clamp(cursorX + offsetX, screenLeft, screenRight - Width);
        _targetTop = Math.Clamp(cursorY + offsetY, screenTop, screenBottom - Height);
    }

    private void PositionAtCursorImmediate()
    {
        UpdateTargetFromCursor();
        Left = _targetLeft;
        Top = _targetTop;
    }

    #endregion

    private void DoneHoldTimer_Tick(object? sender, EventArgs e)
    {
        _doneHoldTimer.Stop();
        AnimateDoneFadeOut();
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        try { File.AppendAllText(LogPath, line + Environment.NewLine); } catch { }
    }
}
