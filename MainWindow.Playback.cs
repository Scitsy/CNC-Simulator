using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FanucSimulator
{
    // Animated playback of a run. The engine has already run the chunk instantly by the time any of
    // this starts; what's here plays the MotionTimeline it recorded back at machine speed, through a
    // PlaybackCursor. Kept apart from MainWindow.xaml.cs, which was already long.
    public partial class MainWindow
    {
        // What the lathe canvas draws: either the engine's live state or a moment of playback.
        private sealed record LatheView(
            StockProfile Stock,
            List<(double X1, double Z1, double X2, double Z2, string Type)> Segments,
            (double X, double Z) ToolRender,
            (double X, double Z) ToolProgrammed,
            bool CoolantOn,
            (double X1, double Z1, double X2, double Z2)? PendingCollision);

        private enum PlaybackState { Idle, Playing, Held }

        private PlaybackState _playState = PlaybackState.Idle;
        private PlaybackCursor? _cursor;
        private Action? _onPlaybackFinished;
        private readonly Stopwatch _frameClock = new();
        private double _lastFrameSeconds;
        private double _playSpeed = 1;
        private (int Messages, int Warnings, int Alarms) _logShown;
        private TimelineEvent? _heldAtCollision;
        private bool _cycleAbortedByEStop;
        private double _lastSlowUiUpdate;

        // True from the moment a run starts playing until it finishes or is reset - "in cycle".
        private bool IsInCycle => _playState != PlaybackState.Idle;

        // How far into the current chunk playback has got, for RUN TIME / CYCLE TIME to tick live.
        private double PlaybackElapsed => _cursor?.Time ?? 0;

        private LatheView CurrentLatheView()
        {
            var segments = new List<(double, double, double, double, string)>();

            if (_cursor == null)
            {
                for (int i = 0; i + 1 < _sim.ToolPath.Count; i += 2)
                    segments.Add((_sim.ToolPath[i].X, _sim.ToolPath[i].Z, _sim.ToolPath[i + 1].X, _sim.ToolPath[i + 1].Z, _sim.ToolPath[i].Type));

                var render = _sim.ToolPath.Count > 0 ? (_sim.ToolPath[^1].X, _sim.ToolPath[^1].Z) : (_sim.X, _sim.Z);
                return new LatheView(_sim.Stock, segments, render, (_sim.X, _sim.Z), _sim.CoolantOn, null);
            }

            // Earlier runs' motion is a fixed backdrop; this run's is drawn up to the playhead.
            var backdrop = Math.Min(_cursor.Timeline.ToolPathStartCount, _sim.ToolPath.Count);
            for (int i = 0; i + 1 < backdrop; i += 2)
                segments.Add((_sim.ToolPath[i].X, _sim.ToolPath[i].Z, _sim.ToolPath[i + 1].X, _sim.ToolPath[i + 1].Z, _sim.ToolPath[i].Type));
            foreach (var seg in _cursor.SegmentsSoFar())
            {
                var type = seg.Kind switch
                {
                    TimelineEventKind.Rapid => "rapid",
                    TimelineEventKind.Collision => "collision",
                    _ => "feed",
                };
                segments.Add((seg.X1, seg.Z1, seg.X2, seg.Z2, type));
            }

            var tool = _cursor.ToolRender ?? (_sim.X, _sim.Z);
            var programmed = _cursor.ToolProgrammed ?? (_sim.X, _sim.Z);
            var coolant = _cursor.State?.CoolantOn ?? _sim.CoolantOn;
            var pending = _heldAtCollision is { } c ? (c.FromRenderX, c.FromRenderZ, c.ToRenderX, c.ToRenderZ) : ((double, double, double, double)?)null;
            return new LatheView(_cursor.Stock, segments, tool, programmed, coolant, pending);
        }

        // The 3D window draws the same moment the 2D canvas does. Framing always uses the engine's
        // whole toolpath, which already holds the complete run, so the camera stays put as playback
        // draws the path out.
        private Stock3DView Current3DView()
        {
            var lathe = CurrentLatheView();
            var drawn = new List<(double X, double Z, string Type)>(lathe.Segments.Count * 2);
            foreach (var seg in lathe.Segments)
            {
                drawn.Add((seg.X1, seg.Z1, seg.Type));
                drawn.Add((seg.X2, seg.Z2, seg.Type));
            }
            return new Stock3DView(lathe.Stock, drawn, lathe.ToolRender, _sim.ToolPath);
        }

        private void AddSegmentPath(
            List<(double X1, double Z1, double X2, double Z2, string Type)> segments,
            string type, Brush stroke, Func<double, double> pxX, Func<double, double> pxY)
        {
            var geometry = new StreamGeometry();
            var any = false;
            using (var ctx = geometry.Open())
            {
                foreach (var s in segments)
                {
                    if (s.Type != type)
                        continue;
                    ctx.BeginFigure(new Point(pxX(s.Z1), pxY(s.X1)), isFilled: false, isClosed: false);
                    ctx.LineTo(new Point(pxX(s.Z2), pxY(s.X2)), isStroked: true, isSmoothJoin: false);
                    any = true;
                }
            }
            if (!any)
                return;
            geometry.Freeze();
            LatheCanvas.Children.Add(new System.Windows.Shapes.Path { Data = geometry, Stroke = stroke, StrokeThickness = 1.5 });
        }

        // Called once the engine has run a chunk. Either plays it back, or - with Animate off, or for
        // a chunk that took no machine time - shows the result at once, as it always used to.
        private void PresentRun(Action onFinished)
        {
            _logShown = (0, 0, 0);
            var timeline = _sim.LastTimeline;
            if (AnimateToggle.IsChecked != true || timeline == null || timeline.Duration <= 0)
            {
                RevealLog(all: true);
                onFinished();
                return;
            }

            _cursor = new PlaybackCursor(timeline);
            _onPlaybackFinished = onFinished;
            _heldAtCollision = null;
            _playState = PlaybackState.Playing;
            _lastFrameSeconds = 0;
            _frameClock.Restart();
            CompositionTarget.Rendering += OnPlaybackFrame;
            SetInCycleLock(true);
            RenderPlaybackFrame(force: true);
        }

        private void OnPlaybackFrame(object? sender, EventArgs e)
        {
            if (_cursor == null)
                return;

            var now = _frameClock.Elapsed.TotalSeconds;
            // Capped so a stall (a dialog, the window being dragged) doesn't jump the tool forward.
            var dt = Math.Min(now - _lastFrameSeconds, 0.25);
            _lastFrameSeconds = now;
            if (_playState != PlaybackState.Playing)
                return;

            var from = _cursor.Time;
            var to = Math.Min(from + dt * _playSpeed, _cursor.Timeline.Duration);

            TimelineEvent? crash = null;
            if (StopOnCollisionToggle.IsChecked == true && _cursor.FirstCollisionBetween(from, to) is double crashTime)
            {
                to = crashTime;
                crash = _cursor.Timeline.Events.First(ev => ev.StartTime == crashTime && ev.Kind == TimelineEventKind.Collision);
            }

            _cursor.Seek(to);

            if (crash != null)
            {
                _heldAtCollision = crash;
                HoldPlayback($"STOPPED BEFORE COLLISION (line {crash.Line})",
                             $"STOPPED BEFORE COLLISION on line {crash.Line} - the dashed red line is the rapid it would have made. CYCLE START continues.");
                return;
            }

            if (_cursor.IsAtEnd)
            {
                FinishPlayback();
                return;
            }

            RenderPlaybackFrame(force: false);
        }

        private void RenderPlaybackFrame(bool force)
        {
            RenderLathe();
            RevealLog(all: false);

            // Text readouts don't need 60 Hz, and rebuilding them every frame is the expensive part.
            var now = _frameClock.Elapsed.TotalSeconds;
            if (force || now - _lastSlowUiUpdate > 0.1)
            {
                _lastSlowUiUpdate = now;
                UpdateDisplay();
            }

            // The 3D view rebuilds its whole revolved mesh on every refresh, so it follows playback
            // at about 8 Hz rather than every frame.
            if (_stock3DWindow?.IsLoaded == true && (force || now - _last3DRefresh > 0.125))
            {
                _last3DRefresh = now;
                _stock3DWindow.Refresh();
            }
        }

        private double _last3DRefresh;

        // 'status' is the short form for the SIM bar; 'detail', if given, is what goes in the console.
        private void HoldPlayback(string status, string? detail = null)
        {
            if (_playState != PlaybackState.Playing)
                return;
            _playState = PlaybackState.Held;
            PlaybackPauseButton.Content = "Resume";
            PlaybackStatusText.Text = status;

            // The log up to this moment first, so the hold line follows whatever it is explaining.
            RevealLog(all: false);
            Log($"[{detail ?? status}]", "info");
            RenderPlaybackFrame(force: true);
        }

        private void ResumePlayback()
        {
            if (_playState != PlaybackState.Held)
                return;
            _heldAtCollision = null;
            _playState = PlaybackState.Playing;
            _lastFrameSeconds = _frameClock.Elapsed.TotalSeconds;
            PlaybackPauseButton.Content = "Pause";
            PlaybackStatusText.Text = "";
        }

        private void FinishPlayback()
        {
            CompositionTarget.Rendering -= OnPlaybackFrame;
            RevealLog(all: true);
            _cursor = null;
            _heldAtCollision = null;
            _playState = PlaybackState.Idle;
            SetInCycleLock(false);
            var finished = _onPlaybackFinished;
            _onPlaybackFinished = null;
            finished?.Invoke();
        }

        // RESET: drop playback on the spot. The caller rebuilds everything else.
        private void AbortPlayback()
        {
            if (_playState == PlaybackState.Idle && _cursor == null)
                return;
            CompositionTarget.Rendering -= OnPlaybackFrame;
            _cursor = null;
            _heldAtCollision = null;
            _onPlaybackFinished = null;
            _playState = PlaybackState.Idle;
            _cycleAbortedByEStop = false;
            SetInCycleLock(false);
        }

        // Console lines, warnings and alarms appear as playback reaches the block that produced them,
        // rather than the whole run's log arriving before the tool has moved.
        private void RevealLog(bool all)
        {
            var target = all || _cursor == null
                ? (_sim.Messages.Count, _sim.Warnings.Count, _sim.Alarms.Count)
                : _cursor.RevealedLog;

            for (int i = _logShown.Messages; i < target.Item1 && i < _sim.Messages.Count; i++)
                Log(_sim.Messages[i], "success");
            for (int i = _logShown.Warnings; i < target.Item2 && i < _sim.Warnings.Count; i++)
                Log(_sim.Warnings[i], "warning");
            for (int i = _logShown.Alarms; i < target.Item3 && i < _sim.Alarms.Count; i++)
                Log(_sim.Alarms[i].ToString(), "error");

            _logShown = (Math.Max(_logShown.Messages, target.Item1),
                         Math.Max(_logShown.Warnings, target.Item2),
                         Math.Max(_logShown.Alarms, target.Item3));
        }

        // While a run is in cycle the engine is already at the end of it, so anything that would run
        // or change the program now would desynchronise what's shown from what the engine holds. A
        // real control refuses these during automatic operation too.
        private void SetInCycleLock(bool inCycle)
        {
            GCodeInput.IsReadOnly = inCycle;
            PlaybackPauseButton.IsEnabled = inCycle;
            PlaybackSkipButton.IsEnabled = inCycle;
            PlaybackPauseButton.Content = "Pause";
            if (!inCycle)
                PlaybackStatusText.Text = "";
        }

        private bool RefuseInCycle(string what)
        {
            if (!IsInCycle)
                return false;
            Log($"{what} is not available while a program is running - FEED HOLD then RESET, or wait for the cycle to end.", "error");
            return true;
        }

        private void PlaybackSpeed_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (PlaybackSpeedCombo.SelectedItem is ComboBoxItem { Tag: string tag } &&
                double.TryParse(tag, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed))
                _playSpeed = speed;
        }

        private void PlaybackPause_Click(object sender, RoutedEventArgs e)
        {
            if (_playState == PlaybackState.Playing)
                HoldPlayback("HOLD");
            else if (_playState == PlaybackState.Held)
                TryResumeCycle();
        }

        private void PlaybackSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_cursor == null)
                return;
            _cursor.Seek(_cursor.Timeline.Duration);
            FinishPlayback();
        }

        // FEED HOLD: stops motion where it is. It was inert while every block ran instantly - there
        // was no mid-move point to hold at. During playback there is.
        private void FeedHold_Click(object sender, RoutedEventArgs e)
        {
            if (_playState == PlaybackState.Playing)
                HoldPlayback("FEED HOLD");
        }

        // CYCLE START while held resumes; refuses after an emergency stop, which needs a RESET.
        private void TryResumeCycle()
        {
            if (_emergencyStop)
            {
                Log("EMERGENCY STOP is engaged - release it, then RESET", "error");
                return;
            }
            if (_cycleAbortedByEStop)
            {
                Log("The cycle was stopped by EMERGENCY STOP - RESET is required before running again", "error");
                return;
            }
            ResumePlayback();
        }

        // An emergency stop during a run halts motion where it is and ends the cycle: on the machine
        // that takes a RESET to recover from, so CYCLE START alone won't continue it here either.
        private void HaltForEmergencyStop()
        {
            if (_playState == PlaybackState.Playing)
                HoldPlayback("EMERGENCY STOP");
            if (IsInCycle)
                _cycleAbortedByEStop = true;
        }
    }
}
