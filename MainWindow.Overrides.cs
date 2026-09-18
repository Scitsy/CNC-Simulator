using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FanucSimulator
{
    // The panel's two override dials, FEED % and RAPID. They are live, as on the machine: the
    // engine records each run at the dial positions of CYCLE START, and playback then speeds up or
    // slows down whatever is still to come as the dials are turned.
    public partial class MainWindow
    {
        // FEED: 0-150 % in 10 % steps. RAPID: F0 (parameter 1421), 25, 50, 100 %. Both from the panel
        // photo (ReferenceMaterial/Real Controller/IMG_20260825_133321_HDR.jpg).
        private static readonly int[] FeedDialPercents = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120, 130, 140, 150 };
        private static readonly int[] RapidDialPercents = { 0, 25, 50, 100 }; // 0 = F0
        private static readonly string[] RapidDialLabels = { "F0", "25", "50", "100%" };

        // Dial angles, degrees, 0 = right, counter-clockwise - laid out like the photo: FEED sweeps
        // from bottom-left (0) over the top to bottom-right (150); RAPID's four stops sit across the top.
        private static double FeedDialAngle(int index) => 215 - index * (250.0 / (FeedDialPercents.Length - 1));
        private static readonly double[] RapidDialAngles = { 160, 115, 65, 20 };

        private int _feedDialIndex = 10;  // 100 %
        private int _rapidDialIndex = 3;  // 100 %

        private double FeedOverrideNow => FeedDialPercents[_feedDialIndex] / 100.0;
        private int RapidOverridePercentNow => RapidDialPercents[_rapidDialIndex];
        private double RapidRateNow => RapidOverridePercentNow <= 0
            ? _parameters.RapidF0MmPerMin
            : MachineSpec.RapidTraverseMmPerMin * RapidOverridePercentNow / 100.0;

        private const double DialCenterX = 48, DialCenterY = 42;
        private RotateTransform? _feedPointer, _rapidPointer;

        private void BuildOverrideDials()
        {
            _feedPointer = BuildDial(FeedDialCanvas, FeedDialPercents.Length, FeedDialAngle,
                i => FeedDialPercents[i] % 50 == 0 ? FeedDialPercents[i].ToString() : null);
            _rapidPointer = BuildDial(RapidDialCanvas, RapidDialPercents.Length, i => RapidDialAngles[i], i => RapidDialLabels[i]);
            UpdateOverrideDials();
        }

        private static RotateTransform BuildDial(Canvas canvas, int stops, Func<int, double> angleOf, Func<int, string?> labelOf)
        {
            canvas.Children.Clear();
            for (int i = 0; i < stops; i++)
            {
                var a = angleOf(i) * Math.PI / 180;
                var (cos, sin) = (Math.Cos(a), -Math.Sin(a));
                canvas.Children.Add(new Line
                {
                    X1 = DialCenterX + 21 * cos, Y1 = DialCenterY + 21 * sin,
                    X2 = DialCenterX + 26 * cos, Y2 = DialCenterY + 26 * sin,
                    Stroke = Brushes.Gainsboro, StrokeThickness = 1.2,
                });
                if (labelOf(i) is string label)
                {
                    var text = new TextBlock { Text = label, Foreground = Brushes.Gainsboro, FontSize = 8 };
                    text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(text, DialCenterX + 34 * cos - text.DesiredSize.Width / 2);
                    Canvas.SetTop(text, DialCenterY + 34 * sin - text.DesiredSize.Height / 2);
                    canvas.Children.Add(text);
                }
            }

            canvas.Children.Add(new Ellipse { Width = 34, Height = 34, Fill = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)), Stroke = Brushes.Gray, StrokeThickness = 1.5 });
            Canvas.SetLeft(canvas.Children[^1], DialCenterX - 17);
            Canvas.SetTop(canvas.Children[^1], DialCenterY - 17);

            var pointer = new RotateTransform(0, DialCenterX, DialCenterY);
            canvas.Children.Add(new Line
            {
                X1 = DialCenterX, Y1 = DialCenterY, X2 = DialCenterX + 18, Y2 = DialCenterY,
                Stroke = Brushes.White, StrokeThickness = 2.5, StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = pointer,
            });
            return pointer;
        }

        private void UpdateOverrideDials()
        {
            if (_feedPointer != null)
                _feedPointer.Angle = -FeedDialAngle(_feedDialIndex);
            if (_rapidPointer != null)
                _rapidPointer.Angle = -RapidDialAngles[_rapidDialIndex];

            var feedText = $"FEED  {FeedDialPercents[_feedDialIndex]}%";
            var rapidText = _rapidDialIndex == 0 ? "RAPID  F0" : $"RAPID  {RapidDialPercents[_rapidDialIndex]}%";
            FeedDialLabel.Text = feedText;
            RapidDialLabel.Text = rapidText;
            AutomationProperties.SetName(FeedDialCanvas, feedText);
            AutomationProperties.SetName(RapidDialCanvas, rapidText);
        }

        // Pick the nearest stop to where the dial was clicked or dragged to.
        private static int NearestStop(Point p, int stops, Func<int, double> angleOf)
        {
            var angle = Math.Atan2(DialCenterY - p.Y, p.X - DialCenterX) * 180 / Math.PI;
            int best = 0;
            double bestDiff = double.MaxValue;
            for (int i = 0; i < stops; i++)
            {
                var diff = Math.Abs(((angle - angleOf(i)) % 360 + 540) % 360 - 180);
                if (diff < bestDiff) (best, bestDiff) = (i, diff);
            }
            return best;
        }

        private void FeedDial_Mouse(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;
            ((UIElement)sender).CaptureMouse();
            SetFeedDial(NearestStop(e.GetPosition(FeedDialCanvas), FeedDialPercents.Length, FeedDialAngle));
        }

        private void RapidDial_Mouse(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;
            ((UIElement)sender).CaptureMouse();
            SetRapidDial(NearestStop(e.GetPosition(RapidDialCanvas), RapidDialPercents.Length, i => RapidDialAngles[i]));
        }

        private void Dial_MouseUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

        // Scrolling turns the dial one stop at a time, clockwise (up) = more.
        private void FeedDial_Wheel(object sender, MouseWheelEventArgs e)
        {
            SetFeedDial(Math.Clamp(_feedDialIndex + Math.Sign(e.Delta), 0, FeedDialPercents.Length - 1));
            e.Handled = true;
        }

        private void RapidDial_Wheel(object sender, MouseWheelEventArgs e)
        {
            SetRapidDial(Math.Clamp(_rapidDialIndex + Math.Sign(e.Delta), 0, RapidDialPercents.Length - 1));
            e.Handled = true;
        }

        private void SetFeedDial(int index)
        {
            if (index == _feedDialIndex)
                return;
            _feedDialIndex = index;
            UpdateOverrideDials();
        }

        private void SetRapidDial(int index)
        {
            if (index == _rapidDialIndex)
                return;
            _rapidDialIndex = index;
            UpdateOverrideDials();
        }

        // What the engine runs the next chunk at. At FEED 0% it is recorded at 100% instead: the
        // machine would never finish a cut at 0%, so there is no time to record - playback holds the
        // tool until the dial comes up.
        private void ApplyOverrides()
        {
            if (_sim == null)
                return;
            _sim.FeedOverride = FeedOverrideNow > 0 ? FeedOverrideNow : 1.0;
            _sim.RapidOverridePercent = RapidOverridePercentNow;
            _sim.RapidF0MmPerMin = _parameters.RapidF0MmPerMin;
        }

        // How fast an event plays now against how it was recorded (see PlaybackCursor.TimeAfter).
        private double PlaybackRate(TimelineEvent ev)
        {
            var timeline = _cursor!.Timeline;
            return ev.Kind switch
            {
                TimelineEventKind.Rapid or TimelineEventKind.Collision => RapidRateNow / timeline.RecordedRapidMmPerMin,
                TimelineEventKind.Feed when ev.FeedOverrideApplies => FeedOverrideNow / timeline.RecordedFeedOverride,
                _ => 1.0,
            };
        }

        // Surface finish goes with feed per rev squared, and the FEED dial scales feed per rev.
        private double PlaybackFinishScale
        {
            get
            {
                var ratio = FeedOverrideNow / _cursor!.Timeline.RecordedFeedOverride;
                return ratio * ratio;
            }
        }
    }
}
