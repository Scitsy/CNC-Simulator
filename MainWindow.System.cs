using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace FanucSimulator
{
    // The SYSTEM screen's parameters, and the surface-finish colours shared by the 2D and 3D views.
    public partial class MainWindow
    {
        // Parameters live in the control, not in a program: they survive RESET (which builds a fresh
        // simulator) and restarts, so they are held here and saved beside the tool offsets.
        private sealed class MachineParameters
        {
            public bool SpindleSpeedArrivalCheck { get; set; } = true;
            public double SpindleRampSeconds { get; set; } = MachineSpec.SpindleRampSecondsTypical;
        }

        private static readonly string ParametersPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanucSimulator", "parameters.json");

        private MachineParameters _parameters = new();

        private void LoadMachineParameters()
        {
            try
            {
                if (File.Exists(ParametersPath))
                    _parameters = JsonSerializer.Deserialize<MachineParameters>(File.ReadAllText(ParametersPath)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                _parameters = new(); // unreadable file: fall back to the defaults rather than fail to start
            }
            ApplyMachineParameters();
        }

        private void SaveMachineParameters()
        {
            try
            {
                Directory.CreateDirectory(IOPath.GetDirectoryName(ParametersPath)!);
                File.WriteAllText(ParametersPath, JsonSerializer.Serialize(_parameters, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log($"Could not save parameters: {ex.Message}", "error");
            }
        }

        private void ApplyMachineParameters()
        {
            if (_sim == null)
                return;
            _sim.SpindleSpeedArrivalCheck = _parameters.SpindleSpeedArrivalCheck;
            _sim.SpindleRampSeconds = _parameters.SpindleRampSeconds;
        }

        private void RefreshSystemScreen()
        {
            SarParameterButton.Content = _parameters.SpindleSpeedArrivalCheck ? "1" : "0";
            SarParameterNote.Text = _parameters.SpindleSpeedArrivalCheck
                ? "1: each cutting move waits until the spindle is up to the commanded speed."
                : "0: cutting starts at once, even while the spindle is still speeding up - the start of those cuts is rougher.";
            SpindleRampInput.Text = _parameters.SpindleRampSeconds.ToString("0.0#");
        }

        // A parameter changed mid-cycle would leave what is showing out of step with the run the
        // engine already finished - refused, like the other edits during a cycle.
        private void SarParameter_Click(object sender, RoutedEventArgs e)
        {
            if (RefuseInCycle("Changing parameters"))
                return;
            _parameters.SpindleSpeedArrivalCheck = !_parameters.SpindleSpeedArrivalCheck;
            ApplyMachineParameters();
            SaveMachineParameters();
            RefreshSystemScreen();
            Log($"Parameter 3708#0 SAR = {(_parameters.SpindleSpeedArrivalCheck ? 1 : 0)}", "info");
        }

        private void SpindleRampInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                CommitSpindleRamp();
        }

        private void SpindleRampInput_LostFocus(object sender, RoutedEventArgs e) => CommitSpindleRamp();

        private void CommitSpindleRamp()
        {
            if (!double.TryParse(SpindleRampInput.Text, out var seconds) || seconds < 0 || seconds > 60)
            {
                RefreshSystemScreen(); // put the real value back
                return;
            }
            if (Math.Abs(seconds - _parameters.SpindleRampSeconds) < 1e-9)
                return;
            if (RefuseInCycle("Changing parameters"))
            {
                RefreshSystemScreen();
                return;
            }
            _parameters.SpindleRampSeconds = seconds;
            ApplyMachineParameters();
            SaveMachineParameters();
            RefreshSystemScreen();
            Log($"Spindle ramp set to {seconds:0.0#} s (0 to {MachineSpec.MaxSpindleRpm:F0} RPM)", "info");
        }

        // ---- Surface finish colours ----

        // The standard Ra grades: N7 (1.6), N8 (3.2), N9 (6.3), and rougher.
        internal static Color FinishColor(double ra) => ra switch
        {
            <= 1.6 => Color.FromRgb(0x3f, 0xd0, 0x6a),
            <= 3.2 => Color.FromRgb(0xc8, 0xe0, 0x4a),
            <= 6.3 => Color.FromRgb(0xf0, 0xa0, 0x30),
            _ => Color.FromRgb(0xe8, 0x40, 0x40),
        };

        private bool ShowFinish => ShowFinishToggle.IsChecked == true;

        private void ShowFinish_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
                return;
            RenderLathe();
            if (_stock3DWindow?.IsLoaded == true)
                _stock3DWindow.Refresh();
        }
    }
}
