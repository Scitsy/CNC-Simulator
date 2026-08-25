using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;

namespace FanucSimulator
{
    public partial class MainWindow : Window
    {
        private GCodeParser _parser = new();
        private LatheSimulator _sim = new(OffsetTables.LoadOrDefault(OffsetsPath));
        private int _resumeIndex = 0;
        private string _currentMode = "EDIT";
        private string _currentScreen = "POS";
        private TextBox? _focusedInput;
        private bool _shiftArmed = false;
        private readonly DispatcherTimer _clockTimer;

        private string? _currentFilePath;
        private bool _isDirty;
        private bool _suppressDirtyTracking;
        private const string BaseTitle = "Fanuc Simulator - 0i-TF Plus";
        private static readonly string RecentFilesPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanucSimulator", "recent.txt");
        private static readonly string OffsetsPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanucSimulator", "tool_offsets.json");
        private static readonly string CustomCatalogPath = IOPath.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FanucSimulator", "custom_tools.json");
        // "CF/USB" removable-media equivalent - a fixed folder the user keeps program files in,
        // rather than wherever Windows last happened to browse to.
        private static readonly string NCFilesPath = @"C:\Building Programs\fanuc-simple\NCFiles";
        private readonly List<string> _recentFiles = new();

        // Canvas zoom/pan: applied on top of RenderLathe's own auto-fit scale rather than replacing
        // it, so the view still frames the whole part by default and these just let the operator push
        // in on one feature. Pan is stored in screen pixels (not mm) since it's a pure on-screen
        // offset from wherever auto-fit would otherwise put things - simplest thing that still works
        // correctly across renders even though the auto-fit scale itself is recomputed from scratch
        // every call (stock/toolpath extents can grow as a program runs).
        private double _canvasZoom = 1.0;
        private double _canvasPanX = 0;
        private double _canvasPanY = 0;
        private bool _isPanning = false;
        private Point _panDragStart;
        private double _panDragStartX, _panDragStartY;
        // Cached from the most recent RenderLathe call so MouseWheel can convert a cursor pixel
        // position to/from world (mm) coordinates without duplicating the auto-fit extent math.
        private double _lastRenderScale = 1, _lastRenderBaseScale = 1, _lastRenderZOriginX, _lastRenderXOriginY, _lastRenderZMin;

        // "CNC MEM" - multiple named programs resident in the control's own memory, distinct from
        // the file-system-backed Save/Load above (which maps to "CF/USB"). O-number -> G-code text.
        private readonly Dictionary<string, string> _programs = new();
        private string? _currentProgramNumber;
        private Action<string>? _pendingCommandLineAction;
        private readonly Stack<SoftKeyMenu> _softKeyStack = new();

        // BG-EDIT: two CNC-MEM programs open side by side. "Active" side tracks whichever box last
        // had focus (stand-in for a real control's SHIFT+Left/Right side switch) - COPY reads the
        // active box's text selection, PASTE inserts into whatever box is active when pressed, so
        // the real workflow is: select text, COPY, click the other box, PASTE.
        private string? _bgEditLeftNumber;
        private string? _bgEditRightNumber;
        private bool _bgEditActiveLeft = true;
        private string _bgEditClipboard = "";
        private bool _suppressBgEditSync;

        // A menu of up to 7 hardware softkeys (Label, Handler); null slots render blank/disabled.
        // Pressing one either performs an action directly or pushes a new SoftKeyMenu (e.g. FOLDER ->
        // OPRT -> CREATE PROGRAM/DELETE/...), mirroring how a real control's softkey row is
        // context-sensitive and stacks deeper as you navigate into sub-menus.
        private class SoftKeyMenu
        {
            public (string Label, Action Handler)?[] Slots = new (string, Action)?[7];
        }

        // Real wall-clock elapsed time, not a physics estimate from feed rate/distance - honest and
        // simple, and consistent with this simulator executing G-code near-instantly (these will
        // mostly read "0H 0M 0S", same as a real control's own idle-state screenshot).
        // Realistic simulated cycle/run time (LatheSimulator.SimulatedSecondsElapsed, computed from
        // actual commanded feed rates/dwells), not wall-clock Stopwatches - the program executes
        // near-instantly regardless of what it commands, so real elapsed UI time was never a
        // meaningful cycle-time estimate. CYCLE resets at the start of each new cycle
        // (_resumeIndex==0); RUN accumulates across the whole session until Reset.
        private double _cycleSimulatedSeconds;
        private double _runSimulatedSeconds;
        private int _partCount = 0;
        private bool _emergencyStop = false;

        // Dual-legend MDI keypad keys, left-to-right/top-to-bottom matching the reference photo.
        // Some secondary legends are approximated where the source image was too small to read
        // with certainty; the primary (unshifted) character typed by each key is exact.
        private static readonly (string Primary, string Secondary)[] DualKeys =
        {
            ("O","P"), ("N","Q"), ("G","R"), ("7","A"), ("8","B"), ("9","D"),
            ("X","C"), ("Z","Y"), ("F","L"), ("4",""), ("5",""), ("6","SP"),
            ("M","I"), ("S","K"), ("T","J"), ("1",","), ("2","#"), ("3","EOB"),
            ("U","H"), ("W","V"), ("+","-"), ("0","*"), (".",""), ("=","")
        };

        public MainWindow()
        {
            InitializeComponent();
            ToolCatalog.LoadCustomEntries(CustomCatalogPath);
            Directory.CreateDirectory(NCFilesPath);
            PopulateHelpScreen();
            BuildDualKeyGrid();
            LoadRecentFilesList();
            RefreshRecentFilesUi();
            SetMode("EDIT");
            SetScreen("POS");
            UpdateDisplay();
            RenderLathe();
            _isDirty = false; // the default sample text isn't a user edit

            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (_, _) =>
            {
                StatusClockDisplay.Text = DateTime.Now.ToString("HH:mm:ss");
                UpdateStatsDisplay();
            };
            _clockTimer.Start();
        }

        private void Execute_Click(object sender, RoutedEventArgs e)
        {
            if (_emergencyStop)
            {
                Log("EMERGENCY STOP is engaged - release it before running", "error");
                return;
            }

            if (_resumeIndex == 0)
            {
                Console.Clear();
                _cycleSimulatedSeconds = 0;
            }

            var blocks = _parser.Parse(GCodeInput.Text);
            var result = _sim.RunProgram(blocks, _resumeIndex);

            _cycleSimulatedSeconds += _sim.SimulatedSecondsElapsed;
            _runSimulatedSeconds += _sim.SimulatedSecondsElapsed;

            foreach (var msg in _sim.Messages)
                Log(msg, "success");
            foreach (var alarm in _sim.Alarms)
                Log(alarm.ToString(), "error");
            foreach (var warning in _sim.Warnings)
                Log(warning, "warning");

            _resumeIndex = result.Paused ? result.NextBlockIndex : 0;
            if (result.Paused)
                Log("[Paused - press Execute to continue]", "info");

            if (result.ProgramEnded)
                _partCount++;

            UpdateDisplay();
            RenderLathe();
            RefreshOffsetGrids();
            RefreshAlarmList();
            RefreshMacroScreen();
            if (_stock3DWindow?.IsLoaded == true) _stock3DWindow.Refresh();
        }

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            // Carries the current offset table forward - a real control's RESET clears the
            // run/alarm state but never touches the tool/work offset tables.
            _sim = new LatheSimulator(_sim.Offsets);
            _resumeIndex = 0;
            Console.Clear();
            _runSimulatedSeconds = 0;
            _cycleSimulatedSeconds = 0;
            _partCount = 0;
            StockDiameterInput.Text = _sim.StockDiameter.ToString("F1");
            StockLengthInput.Text = _sim.StockLength.ToString("F1");
            UpdateDisplay();
            RenderLathe();
            RefreshOffsetGrids();
            RefreshAlarmList();
            RefreshMacroScreen();
            if (_stock3DWindow?.IsLoaded == true)
            {
                _stock3DWindow.UpdateSimulator(_sim); // Reset replaces _sim wholesale - follow along, don't render the discarded instance
                _stock3DWindow.Refresh();
            }
            Log("Reset", "success");
        }

        // Canvas isn't clipped to bounds by default in WPF, so a render computed against a stale
        // ActualHeight (e.g. before the window's own layout - including the operator panel row -
        // has settled) can visually overflow into whatever sits below it. Re-render whenever the
        // canvas's real size actually changes, not just on explicit Execute/Reset/Apply actions.
        private void LatheCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderLathe();

        private void ApplyStock_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(StockDiameterInput.Text, out var dia) && dia > 0)
                _sim.StockDiameter = dia;
            if (double.TryParse(StockLengthInput.Text, out var len) && len > 0)
                _sim.StockLength = len;

            // Changing stock dimensions means chucking a fresh blank, not resizing an already-cut part.
            _sim.ResetStockProfile();
            RenderLathe();
            if (_stock3DWindow?.IsLoaded == true) _stock3DWindow.Refresh();
        }

        // ---- Save / Load ----

        private void GCodeInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressDirtyTracking)
                return;
            _isDirty = true;
            // Memory-resident programs have no separate "save" step on a real control - edits write
            // straight through. Disk files (Save/Load below) stay an explicit, separate action.
            if (_currentProgramNumber != null)
                _programs[_currentProgramNumber] = GCodeInput.Text;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var path = _currentFilePath;
            if (path == null)
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "G-Code Files (*.nc;*.txt)|*.nc;*.txt|All Files (*.*)|*.*",
                    DefaultExt = ".nc",
                    InitialDirectory = NCFilesPath
                };
                if (dialog.ShowDialog() != true)
                    return;
                path = dialog.FileName;
            }

            try
            {
                File.WriteAllText(path, GCodeInput.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, $"Could not save to:\n{path}\n\n{ex.Message}", "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Log($"Save failed: {ex.Message}", "error");
                return;
            }

            _currentFilePath = path;
            _isDirty = false;
            UpdateWindowTitle();
            AddToRecentFiles(path);
            Log($"Saved {IOPath.GetFileName(path)}", "success");
        }

        private void Load_Click(object sender, RoutedEventArgs e)
        {
            if (!ConfirmDiscardUnsavedChanges())
                return;

            var dialog = new OpenFileDialog
            {
                Filter = "G-Code Files (*.nc;*.txt)|*.nc;*.txt|All Files (*.*)|*.*",
                InitialDirectory = NCFilesPath
            };
            if (dialog.ShowDialog() != true)
                return;

            LoadFile(dialog.FileName);
        }

        private void RecentFile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string path)
                return;

            if (!ConfirmDiscardUnsavedChanges())
                return;

            if (!File.Exists(path))
            {
                MessageBox.Show(this, $"File not found:\n{path}", "Load", MessageBoxButton.OK, MessageBoxImage.Warning);
                _recentFiles.Remove(path);
                SaveRecentFilesList();
                RefreshRecentFilesUi();
                return;
            }

            LoadFile(path);
        }

        private void LoadFile(string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, $"Could not load:\n{path}\n\n{ex.Message}", "Load Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Log($"Load failed: {ex.Message}", "error");
                return;
            }

            _suppressDirtyTracking = true;
            GCodeInput.Text = text;
            _suppressDirtyTracking = false;

            _currentFilePath = path;
            _currentProgramNumber = null; // now viewing a disk file, not a CNC MEM program
            _isDirty = false;
            ProgramEditorHeader.Text = "PROGRAM (EDIT):";
            UpdateWindowTitle();
            AddToRecentFiles(path);
            Log($"Loaded {IOPath.GetFileName(path)}", "success");
        }

        // Returns true if it's fine to proceed (no unsaved changes, or the user chose to save/discard).
        private bool ConfirmDiscardUnsavedChanges()
        {
            if (!_isDirty)
                return true;

            var result = MessageBox.Show(this, "The current program has unsaved changes. Save before continuing?",
                "Unsaved Changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Cancel)
                return false;

            if (result == MessageBoxResult.Yes)
                Save_Click(this, new RoutedEventArgs());

            return !_isDirty || result == MessageBoxResult.No;
        }

        // ---- Program-folder navigation (softkey-stack driven) ----

        private SoftKeyMenu MakeProgramBaseMenu()
        {
            var menu = new SoftKeyMenu();
            menu.Slots[0] = ("FOLDER", FolderKey_Pressed);
            return menu;
        }

        // Switching top-level screens always returns the softkey row to that screen's own base menu
        // - only PROGRAM currently drives real navigation; the others stay all-blank for now.
        private void ResetSoftKeysForScreen(string screen)
        {
            _softKeyStack.Clear();
            _softKeyStack.Push(screen == "PROGRAM" ? MakeProgramBaseMenu() : new SoftKeyMenu());
            RenderSoftKeys();
        }

        private void PushSoftKeyMenu(SoftKeyMenu menu)
        {
            _softKeyStack.Push(menu);
            RenderSoftKeys();
        }

        // The base menu (index 0 in the stack) is never popped - it's what "leaving" the deepest
        // sub-menu returns to, same as a real control's softkey row can't go blank on its own screen.
        private void PopSoftKeyMenu()
        {
            if (_softKeyStack.Count > 1)
                _softKeyStack.Pop();
            RenderSoftKeys();
        }

        private void RenderSoftKeys()
        {
            var menu = _softKeyStack.Peek();
            var buttons = new[] { SoftKey0, SoftKey1, SoftKey2, SoftKey3, SoftKey4, SoftKey5, SoftKey6 };
            for (int i = 0; i < buttons.Length; i++)
            {
                var slot = menu.Slots[i];
                buttons[i].Content = slot?.Label ?? "";
                buttons[i].IsEnabled = slot != null;
                buttons[i].Style = (Style)FindResource(slot != null ? "SoftKeyButton" : "DisabledSoftKeyButton");
            }
        }

        private void SoftKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tagText || !int.TryParse(tagText, out var index))
                return;
            _softKeyStack.Peek().Slots[index]?.Handler?.Invoke();
        }

        private void FolderKey_Pressed()
        {
            ProgramEditorView.Visibility = Visibility.Collapsed;
            ProgramBgEditView.Visibility = Visibility.Collapsed;
            ProgramFolderView.Visibility = Visibility.Visible;
            RefreshProgramFolderList();

            var menu = new SoftKeyMenu();
            menu.Slots[0] = ("OPRT", OprtKey_Pressed);
            menu.Slots[6] = ("BACK", BackFromFolder_Pressed);
            PushSoftKeyMenu(menu);
        }

        // Returns the softkey row to the FOLDER list state on top of a freshly-reset base menu -
        // used both by the normal FOLDER key and by BG ALL END, which needs to drop several levels
        // of BG-EDIT sub-navigation at once rather than popping back through them one at a time.
        private void ReturnToFolderFromBaseReset()
        {
            _softKeyStack.Clear();
            _softKeyStack.Push(MakeProgramBaseMenu());
            FolderKey_Pressed();
        }

        private void BackFromFolder_Pressed()
        {
            ProgramFolderView.Visibility = Visibility.Collapsed;
            ProgramEditorView.Visibility = Visibility.Visible;
            PopSoftKeyMenu();
        }

        private void OprtKey_Pressed()
        {
            var menu = new SoftKeyMenu();
            menu.Slots[0] = ("CREATE PROGRAM", CreateProgram_Pressed);
            menu.Slots[1] = ("DELETE", Delete_Pressed);
            menu.Slots[2] = ("RENAME", Rename_Pressed);
            menu.Slots[3] = ("SELECT", Select_Pressed);
            menu.Slots[4] = ("DEVICE CHANGE", DeviceChange_Pressed);
            menu.Slots[5] = ("BG-EDIT", BgEditKey_Pressed);
            menu.Slots[6] = ("BACK", () => PopSoftKeyMenu());
            PushSoftKeyMenu(menu);
        }

        private void RefreshProgramFolderList()
        {
            ProgramFolderList.ItemsSource = _programs.Keys.OrderBy(k => k)
                .Select(number => $"{number,-8} {CountLines(_programs[number]),4} lines")
                .ToList();
        }

        private static int CountLines(string text) =>
            text.Split('\n').Count(line => !string.IsNullOrWhiteSpace(line));

        private string? SelectedFolderProgramNumber()
        {
            if (ProgramFolderList.SelectedIndex < 0)
                return null;
            return _programs.Keys.OrderBy(k => k).ElementAtOrDefault(ProgramFolderList.SelectedIndex);
        }

        // Fanuc O-numbers are "O" followed by digits (e.g. O0123) - accepts the digits alone too,
        // since that's what an operator actually types on a real control.
        private static string? NormalizeProgramNumber(string input)
        {
            input = input.Trim().ToUpperInvariant();
            if (!input.StartsWith("O", StringComparison.Ordinal))
                input = "O" + input;
            var digits = input[1..];
            return digits.Length > 0 && digits.All(char.IsDigit) ? input : null;
        }

        private void OpenProgram(string number)
        {
            _suppressDirtyTracking = true;
            GCodeInput.Text = _programs[number];
            _suppressDirtyTracking = false;

            _currentProgramNumber = number;
            _currentFilePath = null;
            _isDirty = false;
            ProgramEditorHeader.Text = $"PROGRAM (EDIT): {number}";
            UpdateWindowTitle();

            ProgramFolderView.Visibility = Visibility.Collapsed;
            ProgramEditorView.Visibility = Visibility.Visible;
            ResetSoftKeysForScreen("PROGRAM");
        }

        private void CreateProgram_Pressed()
        {
            _pendingCommandLineAction = CreateProgramWithNumber;
            Log("Type the new program number (e.g. O0123) and press Run", "info");
            CommandLineInput.Focus();
        }

        private void CreateProgramWithNumber(string input)
        {
            var number = NormalizeProgramNumber(input);
            if (number == null)
            {
                Log($"Invalid program number '{input}' - expected e.g. O0123", "error");
                return;
            }
            if (_programs.ContainsKey(number))
            {
                Log($"{number} already exists", "error");
                return;
            }

            _programs[number] = "";
            Log($"Created {number}", "success");
            OpenProgram(number);
        }

        private void Delete_Pressed()
        {
            if (SelectedFolderProgramNumber() == null)
            {
                Log("Select a program in the folder list first", "error");
                return;
            }

            var menu = new SoftKeyMenu();
            menu.Slots[0] = ("EXEC", DeleteConfirmed);
            menu.Slots[6] = ("BACK", () => PopSoftKeyMenu());
            PushSoftKeyMenu(menu);
        }

        private void DeleteConfirmed()
        {
            var number = SelectedFolderProgramNumber();
            if (number != null)
            {
                _programs.Remove(number);
                if (_currentProgramNumber == number)
                    _currentProgramNumber = null;
                Log($"Deleted {number}", "success");
                RefreshProgramFolderList();
            }
            PopSoftKeyMenu();
        }

        private void Rename_Pressed()
        {
            if (SelectedFolderProgramNumber() == null)
            {
                Log("Select a program in the folder list first", "error");
                return;
            }
            _pendingCommandLineAction = RenameWithNumber;
            Log("Type the new program number and press Run", "info");
            CommandLineInput.Focus();
        }

        private void RenameWithNumber(string input)
        {
            var oldNumber = SelectedFolderProgramNumber();
            if (oldNumber == null)
                return;

            var newNumber = NormalizeProgramNumber(input);
            if (newNumber == null)
            {
                Log($"Invalid program number '{input}' - expected e.g. O0123", "error");
                return;
            }
            if (_programs.ContainsKey(newNumber))
            {
                Log($"{newNumber} already exists", "error");
                return;
            }

            _programs[newNumber] = _programs[oldNumber];
            _programs.Remove(oldNumber);
            if (_currentProgramNumber == oldNumber)
            {
                _currentProgramNumber = newNumber;
                ProgramEditorHeader.Text = $"PROGRAM (EDIT): {newNumber}";
            }
            Log($"Renamed {oldNumber} to {newNumber}", "success");
            RefreshProgramFolderList();
        }

        private void Select_Pressed()
        {
            var number = SelectedFolderProgramNumber();
            if (number == null)
            {
                Log("Select a program in the folder list first", "error");
                return;
            }
            if (!ConfirmDiscardUnsavedChanges())
                return;

            OpenProgram(number);
            Log($"Opened {number}", "success");
        }

        private void DeviceChange_Pressed()
        {
            var menu = new SoftKeyMenu();
            menu.Slots[0] = ("CF/USB", ImportFromDisk_Pressed);
            menu.Slots[1] = ("CNC MEM", () => { Log("Already viewing CNC MEM", "info"); PopSoftKeyMenu(); });
            menu.Slots[6] = ("BACK", () => PopSoftKeyMenu());
            PushSoftKeyMenu(menu);
        }

        // "CF/USB" maps onto this simulator's real removable-media equivalent: the host filesystem,
        // via the same OS file dialog + safety handling Save/Load already use - not a fake in-app
        // filesystem, since this app has no actual external storage to emulate.
        private void ImportFromDisk_Pressed()
        {
            var dialog = new OpenFileDialog
            {
                Filter = "G-Code Files (*.nc;*.txt)|*.nc;*.txt|All Files (*.*)|*.*",
                InitialDirectory = NCFilesPath
            };
            if (dialog.ShowDialog() != true)
                return;

            string text;
            try
            {
                text = File.ReadAllText(dialog.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                MessageBox.Show(this, $"Could not import:\n{dialog.FileName}\n\n{ex.Message}", "Import Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                Log($"Import failed: {ex.Message}", "error");
                return;
            }

            var number = NormalizeProgramNumber(IOPath.GetFileNameWithoutExtension(dialog.FileName));
            if (number == null || _programs.ContainsKey(number))
                number = GenerateNextProgramNumber();

            _programs[number] = text;
            AddToRecentFiles(dialog.FileName);
            Log($"Imported {IOPath.GetFileName(dialog.FileName)} as {number}", "success");
            RefreshProgramFolderList();
            PopSoftKeyMenu();
            PopSoftKeyMenu();
        }

        private string GenerateNextProgramNumber()
        {
            for (int i = 1; i < 10000; i++)
            {
                var candidate = $"O{i:D4}";
                if (!_programs.ContainsKey(candidate))
                    return candidate;
            }
            return $"O{DateTime.Now.Ticks % 10000:D4}";
        }

        // ---- BG-EDIT: split-screen copy/paste between two CNC-MEM programs ----

        private void BgEditKey_Pressed()
        {
            var number = SelectedFolderProgramNumber();
            if (number == null)
            {
                Log("Select a program in the folder list first", "error");
                return;
            }

            _bgEditLeftNumber = number;
            Log($"BG-EDIT: {number} set as left side. Select the second program, then press SELECT 2ND", "info");

            var menu = new SoftKeyMenu();
            menu.Slots[0] = ("SELECT 2ND", BgEditSelectSecond_Pressed);
            menu.Slots[6] = ("BACK", () => { _bgEditLeftNumber = null; PopSoftKeyMenu(); });
            PushSoftKeyMenu(menu);
        }

        private void BgEditSelectSecond_Pressed()
        {
            var number = SelectedFolderProgramNumber();
            if (number == null)
            {
                Log("Select a program in the folder list first", "error");
                return;
            }
            if (number == _bgEditLeftNumber)
            {
                Log("Select a different program for the right side", "error");
                return;
            }

            EnterBgEdit(_bgEditLeftNumber!, number);
        }

        private void EnterBgEdit(string leftNumber, string rightNumber)
        {
            _bgEditLeftNumber = leftNumber;
            _bgEditRightNumber = rightNumber;
            _bgEditActiveLeft = true;
            _bgEditClipboard = "";

            _suppressBgEditSync = true;
            BgEditLeftBox.Text = _programs[leftNumber];
            BgEditRightBox.Text = _programs[rightNumber];
            _suppressBgEditSync = false;

            BgEditLeftHeader.Text = leftNumber;
            BgEditRightHeader.Text = rightNumber;

            ProgramFolderView.Visibility = Visibility.Collapsed;
            ProgramBgEditView.Visibility = Visibility.Visible;
            UpdateBgEditActiveHighlight();
            BgEditLeftBox.Focus();

            _softKeyStack.Clear();
            _softKeyStack.Push(MakeProgramBaseMenu());
            var menu = new SoftKeyMenu();
            menu.Slots[0] = ("COPY", BgEditCopy_Pressed);
            menu.Slots[1] = ("PASTE", BgEditPaste_Pressed);
            menu.Slots[6] = ("BG ALL END", BgEditEnd_Pressed);
            PushSoftKeyMenu(menu);

            Log($"BG-EDIT: {leftNumber} | {rightNumber}", "success");
        }

        private void BgEditLeftBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _bgEditActiveLeft = true;
            UpdateBgEditActiveHighlight();
        }

        private void BgEditRightBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _bgEditActiveLeft = false;
            UpdateBgEditActiveHighlight();
        }

        private void UpdateBgEditActiveHighlight()
        {
            var active = (Brush)FindResource("ScreenHeader");
            var inactive = (Brush)FindResource("ScreenBorder");
            BgEditLeftBox.BorderBrush = _bgEditActiveLeft ? active : inactive;
            BgEditLeftBox.BorderThickness = new Thickness(_bgEditActiveLeft ? 2 : 1);
            BgEditRightBox.BorderBrush = _bgEditActiveLeft ? inactive : active;
            BgEditRightBox.BorderThickness = new Thickness(_bgEditActiveLeft ? 1 : 2);
        }

        private void BgEditLeftBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressBgEditSync || _bgEditLeftNumber == null)
                return;
            _programs[_bgEditLeftNumber] = BgEditLeftBox.Text;
        }

        private void BgEditRightBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressBgEditSync || _bgEditRightNumber == null)
                return;
            _programs[_bgEditRightNumber] = BgEditRightBox.Text;
        }

        private void BgEditCopy_Pressed()
        {
            var activeBox = _bgEditActiveLeft ? BgEditLeftBox : BgEditRightBox;
            if (string.IsNullOrEmpty(activeBox.SelectedText))
            {
                Log("Select text to copy first", "error");
                return;
            }
            _bgEditClipboard = activeBox.SelectedText;
            Log($"Copied {CountLines(_bgEditClipboard)} line(s)", "success");
        }

        private void BgEditPaste_Pressed()
        {
            if (string.IsNullOrEmpty(_bgEditClipboard))
            {
                Log("Nothing copied yet - select text and press COPY first", "error");
                return;
            }
            var targetBox = _bgEditActiveLeft ? BgEditLeftBox : BgEditRightBox;
            var caret = targetBox.CaretIndex;
            targetBox.Text = targetBox.Text.Insert(caret, _bgEditClipboard);
            targetBox.CaretIndex = caret + _bgEditClipboard.Length;
            Log("Pasted", "success");
        }

        private void BgEditEnd_Pressed()
        {
            ProgramBgEditView.Visibility = Visibility.Collapsed;
            _bgEditLeftNumber = null;
            _bgEditRightNumber = null;
            _bgEditClipboard = "";

            ReturnToFolderFromBaseReset();
            Log("BG-EDIT ended", "info");
        }

        private void UpdateWindowTitle()
        {
            Title = _currentFilePath != null ? $"{BaseTitle} - {IOPath.GetFileName(_currentFilePath)}" : BaseTitle;
        }

        private void LoadRecentFilesList()
        {
            _recentFiles.Clear();
            if (File.Exists(RecentFilesPath))
                _recentFiles.AddRange(File.ReadAllLines(RecentFilesPath).Where(File.Exists).Take(8));
        }

        private void SaveRecentFilesList()
        {
            var dir = IOPath.GetDirectoryName(RecentFilesPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllLines(RecentFilesPath, _recentFiles);
        }

        private void AddToRecentFiles(string path)
        {
            _recentFiles.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            _recentFiles.Insert(0, path);
            while (_recentFiles.Count > 8)
                _recentFiles.RemoveAt(_recentFiles.Count - 1);

            SaveRecentFilesList();
            RefreshRecentFilesUi();
        }

        private void RefreshRecentFilesUi()
        {
            RecentFilesPanel.Children.Clear();
            foreach (var path in _recentFiles)
            {
                var button = new Button
                {
                    Content = IOPath.GetFileName(path),
                    Tag = path,
                    ToolTip = path,
                    Style = (Style)FindResource("PanelButton")
                };
                // Stable per-file id (not positional, and not string.GetHashCode() which is
                // randomized per-process) so it survives both the list reshuffling that happens
                // every time a file is loaded and moves to the front, and app restarts.
                AutomationProperties.SetAutomationId(button, "RecentFile_" + IOPath.GetFileName(path));
                button.Click += RecentFile_Click;
                RecentFilesPanel.Children.Add(button);
            }
        }

        private void RunMdi_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CommandLineInput.Text))
                return;

            // CREATE PROGRAM/RENAME park a pending action here and reuse the command line as the
            // real control's own "type the O-number, press a key to confirm" prompt - the input is
            // consumed as that argument instead of being parsed as G-code.
            if (_pendingCommandLineAction != null)
            {
                var input = CommandLineInput.Text.Trim();
                var action = _pendingCommandLineAction;
                _pendingCommandLineAction = null;
                CommandLineInput.Clear();
                action(input);
                return;
            }

            var blocks = _parser.Parse(CommandLineInput.Text);
            var result = _sim.RunProgram(blocks);

            foreach (var msg in _sim.Messages)
                Log(msg, "success");
            foreach (var alarm in _sim.Alarms)
                Log(alarm.ToString(), "error");
            foreach (var warning in _sim.Warnings)
                Log(warning, "warning");

            CommandLineInput.Clear();
            UpdateDisplay();
            RenderLathe();
            RefreshOffsetGrids();
            RefreshAlarmList();
            RefreshMacroScreen();
            if (_stock3DWindow?.IsLoaded == true) _stock3DWindow.Refresh();
        }

        private void ModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string mode)
                SetMode(mode);
        }

        private void SetMode(string mode)
        {
            _currentMode = mode;
            StatusModeDisplay.Text = mode;

            foreach (var button in new[] { ModeEditButton, ModeAutoButton, ModeMdiButton })
                button.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a));

            var active = mode switch
            {
                "EDIT" => ModeEditButton,
                "AUTO" => ModeAutoButton,
                "MDI" => ModeMdiButton,
                _ => null
            };
            if (active != null)
                active.Background = new SolidColorBrush(Color.FromRgb(0x00, 0x44, 0x00));
        }

        private void ScreenButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string screen)
                SetScreen(screen);
        }

        private void SetScreen(string screen)
        {
            _currentScreen = screen;

            PosScreen.Visibility = screen == "POS" ? Visibility.Visible : Visibility.Collapsed;
            ProgramScreen.Visibility = screen == "PROGRAM" ? Visibility.Visible : Visibility.Collapsed;
            OffsetScreen.Visibility = screen == "OFFSET" ? Visibility.Visible : Visibility.Collapsed;
            AlarmScreen.Visibility = screen == "ALARM" ? Visibility.Visible : Visibility.Collapsed;
            MacroScreen.Visibility = screen == "MACRO" ? Visibility.Visible : Visibility.Collapsed;
            HelpScreen.Visibility = screen == "HELP" ? Visibility.Visible : Visibility.Collapsed;

            foreach (var button in new[] { PosTabButton, ProgramTabButton, OffsetTabButton, AlarmTabButton, MacroTabButton, HelpTabButton })
                button.Style = (Style)FindResource("SoftKeyButton");

            var active = screen switch
            {
                "POS" => PosTabButton,
                "PROGRAM" => ProgramTabButton,
                "OFFSET" => OffsetTabButton,
                "ALARM" => AlarmTabButton,
                "MACRO" => MacroTabButton,
                "HELP" => HelpTabButton,
                _ => null
            };
            if (active != null)
                active.Style = (Style)FindResource("ActiveSoftKeyButton");

            if (screen == "OFFSET")
                RefreshOffsetGrids();
            if (screen == "ALARM")
                RefreshAlarmList();
            if (screen == "MACRO")
                RefreshMacroScreen();
            if (screen == "POS")
                UpdateDisplay();

            // Switching top-level screens always returns the hardware softkey row to that screen's
            // base menu - matches how a real control drops any FOLDER/OPRT sub-navigation the moment
            // you leave the screen that owns it.
            if (screen == "PROGRAM")
            {
                ProgramFolderView.Visibility = Visibility.Collapsed;
                ProgramBgEditView.Visibility = Visibility.Collapsed;
                ProgramEditorView.Visibility = Visibility.Visible;
            }
            ResetSoftKeysForScreen(screen);
        }

        private void RefreshOffsetGrids()
        {
            ToolOffsetGrid.ItemsSource = _sim.Offsets.Tools.Values.OrderBy(t => t.Number).ToList();
            WorkOffsetGrid.ItemsSource = _sim.Offsets.WorkOffsets.Values.OrderBy(w => w.GCode).ToList();

            // Rebuilt every call (cheap - a couple dozen strings) rather than cached, so a tool
            // built and added to the catalog via the Tool Builder window shows up here too.
            ToolCatalogList.ItemsSource = ToolCatalog.Entries
                .Select(e => $"{e.InsertDesignation} / {e.HolderDesignation} - {e.Description}")
                .ToList();

            if (AssignToolNumberCombo.ItemsSource == null)
            {
                AssignToolNumberCombo.ItemsSource = Enumerable.Range(1, 8).ToList();
                AssignToolNumberCombo.SelectedIndex = 0;
            }
        }

        private void AssignCatalogEntry_Click(object sender, RoutedEventArgs e)
        {
            if (ToolCatalogList.SelectedIndex < 0)
            {
                MessageBox.Show("Select a catalog entry first.", "No Tool Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (AssignToolNumberCombo.SelectedItem is not int toolNumber)
                return;

            var entry = ToolCatalog.Entries[ToolCatalogList.SelectedIndex];
            var tool = _sim.Offsets.GetOrCreateTool(toolNumber);
            tool.AssignFromCatalog(entry);
            RefreshOffsetGrids();
            SaveOffsets();
        }

        // Modeless and reused across clicks (checked via IsLoaded, which goes false once the user
        // closes it) so repeated "Tool Builder..." clicks don't stack up duplicate windows.
        private ToolBuilderWindow? _toolBuilderWindow;

        private void OpenToolBuilder_Click(object sender, RoutedEventArgs e)
        {
            if (_toolBuilderWindow == null || !_toolBuilderWindow.IsLoaded)
            {
                _toolBuilderWindow = new ToolBuilderWindow(_sim.Offsets) { Owner = this };
                _toolBuilderWindow.ToolApplied += () =>
                {
                    RefreshOffsetGrids();
                    RenderLathe();
                    SaveOffsets();
                    ToolCatalog.SaveCustomEntries(CustomCatalogPath);
                };
            }
            _toolBuilderWindow.Show();
            _toolBuilderWindow.Activate();
        }

        // Modeless, single-instance-reused exactly like _toolBuilderWindow above.
        private Stock3DWindow? _stock3DWindow;

        private void Open3DView_Click(object sender, RoutedEventArgs e)
        {
            if (_stock3DWindow == null || !_stock3DWindow.IsLoaded)
                _stock3DWindow = new Stock3DWindow(_sim) { Owner = this };
            else
                _stock3DWindow.Refresh(); // already open - bring it up to date with the current stock
            _stock3DWindow.Show();
            _stock3DWindow.Activate();
        }

        private void OffsetGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit)
                return;
            // Defer until after WPF has flushed the edited cell value into the bound object.
            Dispatcher.BeginInvoke(new Action(SaveOffsets), DispatcherPriority.Background);
        }

        private void SaveOffsets()
        {
            try
            {
                _sim.Offsets.SaveToFile(OffsetsPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Log($"Could not save tool offsets: {ex.Message}", "error");
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            SaveOffsets();
        }

        private void RefreshAlarmList()
        {
            // Bound to Alarm objects (Number/Message as separate DataGrid columns) so alarm numbers
            // line up in a column instead of running together in one string.
            //
            // .ToList() matters: _sim.Alarms is the same List instance for the whole life of a
            // LatheSimulator, and a plain List raises no collection-changed notification. Assigning
            // that same reference back leaves the grid showing whatever it latched onto the first
            // time, so alarms raised later in a run silently never appear - which is exactly what
            // happened when a program raised two alarms and only the first was displayed. Handing
            // WPF a fresh snapshot each refresh forces it to re-read.
            AlarmList.ItemsSource = _sim.Alarms.ToList();
            StatusAlarmBadge.Visibility = _sim.Alarms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshMacroScreen()
        {
            MacroLocalGrid.ItemsSource = _sim.GetLocalVariableRows();
            var commonRows = _sim.GetCommonVariableRows();
            MacroCommonGrid.ItemsSource = commonRows;
            MacroCommonGrid.Visibility = commonRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MacroCommonEmptyLabel.Visibility = commonRows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        // Lets an operator poke a variable's value directly - handy for debugging a macro mid-
        // development without re-running the whole program. Both grids' rows are freshly-built
        // MacroVariableRow DTOs (see GetLocalVariableRows/GetCommonVariableRows), not live-bound to
        // the simulator's own variable storage, so a committed edit has to be pushed through
        // LatheSimulator.SetVariable explicitly and the grids re-pulled from the simulator afterward
        // - editing the DTO alone would look like it worked but silently not affect the running macro.
        private void MacroGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not MacroVariableRow row)
                return;

            // Defer until after WPF has flushed the edited cell value into the bound row object.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (row.Value is double value && int.TryParse(row.Variable.TrimStart('#'), out var number))
                {
                    // _sim.Messages/Alarms are only cleared at the top of RunProgram, not here - log
                    // just the entries this one edit adds (by index), not the whole accumulated list,
                    // or a second edit would re-log everything from the first one too.
                    var messagesBefore = _sim.Messages.Count;
                    var alarmsBefore = _sim.Alarms.Count;
                    _sim.SetVariable(number, value);
                    for (int i = messagesBefore; i < _sim.Messages.Count; i++)
                        Log(_sim.Messages[i], "success");
                    for (int i = alarmsBefore; i < _sim.Alarms.Count; i++)
                        Log(_sim.Alarms[i].ToString(), "error");
                    if (_sim.Alarms.Count > alarmsBefore)
                        RefreshAlarmList();
                }
                RefreshMacroScreen();
            }), DispatcherPriority.Background);
        }

        private void PopulateHelpScreen()
        {
            HelpContent.Children.Add(MakeHelpHeader("G-CODES"));
            foreach (var kv in GCodeReference.GCodes.OrderBy(k => k.Key))
                HelpContent.Children.Add(MakeHelpEntry(kv.Key, kv.Value));

            HelpContent.Children.Add(MakeHelpHeader("M-CODES"));
            foreach (var kv in GCodeReference.MCodes.OrderBy(k => k.Key))
                HelpContent.Children.Add(MakeHelpEntry(kv.Key, kv.Value));

            HelpContent.Children.Add(MakeHelpHeader("SETUP TIPS & TRICKS"));
            foreach (var tip in GCodeReference.SetupTips)
                HelpContent.Children.Add(MakeHelpEntry(tip.Title, tip.Body));
        }

        // HELP text colours have to be picked against the screen background, and these were chosen
        // back when that was a light grey-olive LCD - near-black body text on the blue CRT the real
        // machine actually uses would be barely legible. Amber headers / cyan titles / white body
        // keep the same three-level hierarchy with contrast that works on blue.
        private static readonly SolidColorBrush HelpHeaderBrush = new(Color.FromRgb(0xff, 0xd2, 0x7f));
        private static readonly SolidColorBrush HelpTitleBrush = new(Color.FromRgb(0xa8, 0xe8, 0xff));
        private static readonly SolidColorBrush HelpBodyBrush = new(Color.FromRgb(0xff, 0xff, 0xff));

        private static TextBlock MakeHelpHeader(string text) => new()
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = HelpHeaderBrush,
            Margin = new Thickness(0, 12, 0, 4)
        };

        private static StackPanel MakeHelpEntry(string title, string body)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.Bold, Foreground = HelpTitleBrush });
            panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Foreground = HelpBodyBrush, FontSize = 12 });
            return panel;
        }

        // POS(ALL)'s ruled boxes column-align their numbers under each other, which only works with
        // the right-aligned padding a real control's fixed-width screen font gives for free.
        private static string AxisLine(char axis, double value) => $"{axis} {value,10:F3}";

        // The O-number the real control shows top-right comes from the program itself, not the file
        // name - so read it back out of the editor text the same way the control would. Falls back to
        // O0000 for a program that never declared one (perfectly legal, just unnamed in memory).
        private string CurrentProgramNumber()
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                GCodeInput.Text, @"^\s*O(\d{1,5})", System.Text.RegularExpressions.RegexOptions.Multiline);
            return match.Success ? $"O{int.Parse(match.Groups[1].Value):D4}" : "O0000";
        }

        // First few program lines, for POS(ALL)'s program pane. Deliberately no highlighted "current"
        // line: a real control highlights the block it is executing, but this simulator runs a whole
        // program to completion inside one Execute click, so there is never a genuine current block
        // to point at - faking one would just be decoration.
        private string ProgramPreviewText()
        {
            var lines = GCodeInput.Text.Replace("\r\n", "\n").Split('\n');
            return string.Join(Environment.NewLine, lines.Take(9).Select(l => l.TrimEnd()));
        }

        private void UpdateDisplay()
        {
            AbsXDisplay.Text = AxisLine('X', _sim.X);
            AbsZDisplay.Text = AxisLine('Z', _sim.Z);
            AbsXBigDisplay.Text = _sim.X.ToString("F3");
            AbsZBigDisplay.Text = _sim.Z.ToString("F3");

            // No separate relative-counter origin is modelled (a real control lets the operator zero
            // U/W independently of the work offset), so RELATIVE tracks ABSOLUTE - which is also what
            // the reference photo of the real machine happens to show.
            RelUDisplay.Text = AxisLine('U', _sim.X);
            RelWDisplay.Text = AxisLine('W', _sim.Z);

            // Blocks run to completion within a single RunProgram call, so at rest there is never any
            // residual commanded motion left to report - always zero, same as the idle real machine.
            DistXDisplay.Text = AxisLine('X', 0);
            DistZDisplay.Text = AxisLine('Z', 0);

            var workOffset = _sim.Offsets.WorkOffsets.TryGetValue(_sim.Modal.ActiveWorkOffset, out var wo) ? wo : null;
            var machineX = _sim.X + (workOffset?.X ?? 0);
            var machineZ = _sim.Z + (workOffset?.Z ?? 0);
            MachXDisplay.Text = AxisLine('X', machineX);
            MachZDisplay.Text = AxisLine('Z', machineZ);

            ModalMotionDisplay.Text = _sim.Modal.Motion == MotionMode.Rapid ? "G00" : "G01";
            // System A has no absolute/incremental modal group; this slot on the real screen carries
            // the canned-cycle group instead, which is G80 whenever no single cycle is armed.
            ModalPositionDisplay.Text = _sim.Modal.Cycle switch
            {
                CannedCycle.Turning => "G90",
                CannedCycle.Threading => "G92",
                CannedCycle.Facing => "G94",
                _ => "G80",
            };
            ModalUnitsDisplay.Text = _sim.Modal.Units == UnitsMode.Metric ? "G21" : "G20";
            ModalFeedDisplay.Text = _sim.Modal.Feed == FeedMode.PerRevolution ? "G99" : "G98";
            ModalSpindleDisplay.Text = _sim.Modal.Spindle == SpindleMode.ConstantSurfaceSpeed ? "G96" : "G97";
            ModalCompDisplay.Text = _sim.Modal.Comp switch { CutterComp.Left => "G41", CutterComp.Right => "G42", _ => "G40" };
            ModalWorkOffsetDisplay.Text = $"G{_sim.Modal.ActiveWorkOffset}";
            ModalToolDisplay.Text = $"T{_sim.CurrentTool:D2}";
            // Coolant belongs in the modal block as its M-code, the way a real control lists it -
            // spelling out "COOLANT ON/OFF" overflowed the column and isn't what the machine shows.
            ModalCoolantDisplay.Text = _sim.CoolantOn ? "M08" : "M09";

            // The M field on a real POS screen shows the M-code currently in effect; spindle
            // direction is the only continuously-held M state this simulator actually tracks.
            ModalMCodeDisplay.Text = "M   " + _sim.SpindleDir switch { 1 => "03", -1 => "04", _ => "05" };

            var feedLabel = _sim.Modal.Feed == FeedMode.PerRevolution ? "mm/rev" : "mm/min";
            FeedDisplay.Text = $"{_sim.FeedRate:F2} {feedLabel}";
            AllFeedDisplay.Text = $"{_sim.FeedRate:F2} {feedLabel}";

            var dirLabel = _sim.SpindleDir switch { 1 => "FWD", -1 => "REV", _ => "STOPPED" };
            var modeLabel = _sim.Modal.Spindle == SpindleMode.ConstantSurfaceSpeed ? "CSS" : "RPM";
            SpindleDisplay.Text = _sim.SpindleDir != 0 ? $"{_sim.SpindleSpeed:F0} RPM ({modeLabel}) {dirLabel}" : "STOPPED";
            AllSpindleDisplay.Text = $"S {_sim.SpindleSpeed,8:F0}      {dirLabel}";

            ProgramIdDisplay.Text = $"{CurrentProgramNumber()} N00000";
            AllProgramPreview.Text = ProgramPreviewText();

            UpdateStatsDisplay();
            UpdateEmgBadge();
        }

        private static string FormatHms(TimeSpan t) => $"{(int)t.TotalHours}H {t.Minutes}M {t.Seconds}S";

        private void UpdateStatsDisplay()
        {
            RunTimeDisplay.Text = FormatHms(TimeSpan.FromSeconds(_runSimulatedSeconds));
            CycleTimeDisplay.Text = FormatHms(TimeSpan.FromSeconds(_cycleSimulatedSeconds));
            PartCountDisplay.Text = _partCount.ToString();

            // POS(ALL) carries its own copy of these in the right-hand column (the two sub-views lay
            // them out completely differently), so both sets are written from the same values here.
            AllRunTimeDisplay.Text = RunTimeDisplay.Text;
            AllCycleTimeDisplay.Text = CycleTimeDisplay.Text;
            AllPartCountDisplay.Text = PartCountDisplay.Text;
        }

        private void CoolantToggle_Click(object sender, RoutedEventArgs e)
        {
            _sim.CoolantOn = !_sim.CoolantOn;
            UpdateDisplay();
            RenderLathe();
        }

        private void EStop_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _emergencyStop = !_emergencyStop;
            UpdateEmgBadge();
            Log(_emergencyStop ? "EMERGENCY STOP engaged" : "EMERGENCY STOP released", _emergencyStop ? "error" : "success");
        }

        private void PowerToggle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            // Decorative only - no engine effect, matches the reference panel's own power switch.
        }

        private void UpdateEmgBadge()
        {
            if (_emergencyStop)
            {
                StatusEmgBadge.Background = (Brush)FindResource("AlarmRed");
                StatusEmgText.Text = "EMG STOP";
                StatusEmgText.Foreground = Brushes.White;
            }
            else
            {
                StatusEmgBadge.Background = Brushes.Transparent;
                StatusEmgText.Text = "--EMG--";
                StatusEmgText.Foreground = (Brush)FindResource("ScreenHeader");
            }
        }

        private void PosSubTab_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string view)
                return;

            var showAll = view == "ALL";
            PosAbsView.Visibility = showAll ? Visibility.Collapsed : Visibility.Visible;
            PosAbsStats.Visibility = showAll ? Visibility.Collapsed : Visibility.Visible;
            PosAllView.Visibility = showAll ? Visibility.Visible : Visibility.Collapsed;
            PosHeaderDisplay.Text = showAll ? "POSITION(ALL)" : "POSITION(ABS)";

            PosAbsSubTab.Style = (Style)FindResource(showAll ? "SoftKeyButton" : "ActiveSoftKeyButton");
            PosAllSubTab.Style = (Style)FindResource(showAll ? "ActiveSoftKeyButton" : "SoftKeyButton");
        }

        private static readonly SolidColorBrush CanvasBgNormal = new(Color.FromRgb(0x0a, 0x0a, 0x0a));
        private static readonly SolidColorBrush CanvasBgCoolant = new(Color.FromRgb(0x18, 0x30, 0x38));

        // Shared with the MouseWheel zoom handler, which needs the same margin the auto-fit math
        // uses to convert a cursor pixel position back to world (mm) coordinates.
        private const double CanvasLeftMargin = 70;   // chuck + spindle label
        private const double CanvasRightMargin = 70;  // clearance moves + position label
        private const double CanvasTopMargin = 50;    // spindle label above max diameter
        private const double CanvasBottomMargin = 20;

        private void RenderLathe()
        {
            LatheCanvas.Children.Clear();
            LatheCanvas.Background = _sim.CoolantOn ? CanvasBgCoolant : CanvasBgNormal;

            // Auto-fit scale: a fixed 2px/mm scale either ran huge parts off-canvas or squeezed small
            // ones (e.g. ID drilling/boring, typically a fraction of the OD) into a barely-visible
            // sliver near the centerline. Instead, size the view from what's actually there - the
            // stock envelope plus wherever the toolpath has actually gone (so retract/clearance moves
            // stay visible too) - and use one uniform scale for both axes so the cross-section isn't
            // visually stretched out of proportion. _canvasZoom/_canvasPanX/_canvasPanY then layer a
            // manual zoom/pan on top (see LatheCanvas_MouseWheel / drag handlers below) without
            // disturbing this default framing.
            var canvasWidth = LatheCanvas.ActualWidth > 0 ? LatheCanvas.ActualWidth : 650;
            var canvasHeight = LatheCanvas.ActualHeight > 0 ? LatheCanvas.ActualHeight : 750;

            const double leftMargin = CanvasLeftMargin;
            const double rightMargin = CanvasRightMargin;
            const double topMargin = CanvasTopMargin;
            const double bottomMargin = CanvasBottomMargin;

            var toolPathMaxX = _sim.ToolPath.Count > 0 ? _sim.ToolPath.Max(p => p.X) : 0;
            var toolPathMinZ = _sim.ToolPath.Count > 0 ? _sim.ToolPath.Min(p => p.Z) : 0;
            var toolPathMaxZ = _sim.ToolPath.Count > 0 ? _sim.ToolPath.Max(p => p.Z) : 0;

            var xExtent = Math.Max(_sim.StockDiameter, toolPathMaxX);
            var zMin = Math.Min(-_sim.StockLength, toolPathMinZ);
            var zMax = Math.Max(0, toolPathMaxZ);
            var zExtent = Math.Max(1, zMax - zMin);

            var availableWidth = Math.Max(50, canvasWidth - leftMargin - rightMargin);
            var availableHeight = Math.Max(50, canvasHeight - topMargin - bottomMargin);
            var baseScale = Math.Min(availableWidth / zExtent, availableHeight / Math.Max(1, xExtent));
            var scale = baseScale * _canvasZoom;

            var zOriginX = leftMargin - zMin * scale + _canvasPanX; // pixel X where Z=0 falls
            var xOriginY = canvasHeight - bottomMargin + _canvasPanY; // pixel Y where X=0 (centerline) falls

            _lastRenderScale = scale;
            _lastRenderBaseScale = baseScale;
            _lastRenderZOriginX = zOriginX;
            _lastRenderXOriginY = xOriginY;
            _lastRenderZMin = zMin;

            double PxX(double z) => zOriginX + z * scale;
            double PxY(double x) => xOriginY - x * scale;

            // Grid: horizontal lines at diameter graduations, spaced to suit the part's actual size.
            var gridStep = xExtent switch { <= 30 => 5, <= 80 => 10, <= 200 => 25, _ => 50 };
            for (double x = 0; x <= xExtent + gridStep; x += gridStep)
            {
                var y = PxY(x);
                var line = new Line
                {
                    X1 = 20,
                    Y1 = y,
                    X2 = canvasWidth - 20,
                    Y2 = y,
                    Stroke = Brushes.DarkGray,
                    StrokeThickness = 1
                };
                LatheCanvas.Children.Add(line);
            }

            // Stock: spans Z=-StockLength (chuck end, left) to Z0 (face). Traced from the carved
            // profile (Stock.OuterX/InnerX) rather than a fixed block, so turned/faced/bored/drilled
            // material actually disappears as the program runs instead of just being drawn over.
            var stockPixelDiameter = _sim.StockDiameter * scale;
            var stockLeft = PxX(-_sim.StockLength);

            var stock = _sim.Stock;
            var stockPoints = new PointCollection();
            for (int i = 0; i <= StockProfile.Resolution; i++)
                stockPoints.Add(new Point(PxX(stock.SampleZ(i)), PxY(stock.OuterX[i])));
            for (int i = StockProfile.Resolution; i >= 0; i--)
                stockPoints.Add(new Point(PxX(stock.SampleZ(i)), PxY(stock.InnerX[i])));

            var workpiece = new Polygon
            {
                Points = stockPoints,
                Fill = new SolidColorBrush(Color.FromArgb(255, 42, 74, 42)),
                Stroke = Brushes.DarkGreen,
                StrokeThickness = 2
            };
            LatheCanvas.Children.Add(workpiece);

            // Chuck/spindle indicator at the stock's held (-Z) end, to the left - taller than the
            // stock OD, sitting on the same centerline baseline.
            var chuckHeight = stockPixelDiameter + 20;
            var chuck = new Rectangle
            {
                Width = 20,
                Height = chuckHeight,
                Fill = new SolidColorBrush(Color.FromRgb(55, 55, 60)),
                Stroke = Brushes.Gray,
                StrokeThickness = 1.5
            };
            Canvas.SetLeft(chuck, stockLeft - 20);
            Canvas.SetTop(chuck, xOriginY - chuckHeight);
            LatheCanvas.Children.Add(chuck);

            var chuckLabel = new TextBlock
            {
                Text = "SPINDLE",
                Foreground = Brushes.Gray,
                FontSize = 9
            };
            Canvas.SetLeft(chuckLabel, stockLeft - 30);
            Canvas.SetTop(chuckLabel, xOriginY - chuckHeight - 14);
            LatheCanvas.Children.Add(chuckLabel);

            // Z0 reference line at the stock face.
            var zeroLine = new Line
            {
                X1 = zOriginX,
                Y1 = xOriginY - stockPixelDiameter - 20,
                X2 = zOriginX,
                Y2 = xOriginY + 10,
                Stroke = Brushes.DimGray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            LatheCanvas.Children.Add(zeroLine);

            // Tool path - each move contributes an independent (from, to) pair, stepping by 2 so we
            // don't draw a spurious seam line between one move's end and the next move's start (their
            // rendered positions can differ slightly when cutter comp direction changes between moves).
            for (int i = 0; i < _sim.ToolPath.Count - 1; i += 2)
            {
                var p1 = _sim.ToolPath[i];
                var p2 = _sim.ToolPath[i + 1];
                var stroke = p1.Type switch
                {
                    "rapid" => Brushes.DeepSkyBlue,
                    "collision" => Brushes.Red, // not produced yet - reserved for the future tool-library collision check
                    _ => Brushes.LimeGreen
                };

                var line = new Line
                {
                    X1 = PxX(p1.Z),
                    Y1 = PxY(p1.X),
                    X2 = PxX(p2.Z),
                    Y2 = PxY(p2.X),
                    Stroke = stroke,
                    StrokeThickness = 1.5
                };
                LatheCanvas.Children.Add(line);
            }

            // Current position
            var tool = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.Lime
            };
            Canvas.SetLeft(tool, PxX(_sim.Z) - 4);
            Canvas.SetTop(tool, PxY(_sim.X) - 4);
            LatheCanvas.Children.Add(tool);

            // Position label
            var label = new TextBlock
            {
                Text = $"X:{_sim.X:F1} Z:{_sim.Z:F1}",
                Foreground = Brushes.Lime,
                FontSize = 10
            };
            Canvas.SetLeft(label, PxX(_sim.Z) + 10);
            Canvas.SetTop(label, PxY(_sim.X) - 20);
            LatheCanvas.Children.Add(label);

            ZoomLabel.Text = $"{_canvasZoom * 100:F0}%";
        }

        // ---- Canvas zoom/pan ----

        private void LatheCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var pos = e.GetPosition(LatheCanvas);

            // World (mm) point currently under the cursor, from the last render's own mapping.
            var worldZ = (pos.X - _lastRenderZOriginX) / _lastRenderScale;
            var worldX = (_lastRenderXOriginY - pos.Y) / _lastRenderScale;

            var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
            _canvasZoom = Math.Clamp(_canvasZoom * factor, 0.2, 20);

            // Re-derive pan so that same world point stays under the cursor after the zoom change -
            // reuses the last render's base scale/extents (auto-fit itself doesn't change between
            // wheel ticks, only _canvasZoom/_canvasPanX/Y do).
            var newScale = _lastRenderBaseScale * _canvasZoom;
            _canvasPanX = pos.X - CanvasLeftMargin + newScale * (_lastRenderZMin - worldZ);
            _canvasPanY = pos.Y + worldX * newScale - LatheCanvas.ActualHeight + CanvasBottomMargin;

            RenderLathe();
            e.Handled = true;
        }

        private void LatheCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                ResetCanvasView();
                return;
            }

            _isPanning = true;
            _panDragStart = e.GetPosition(LatheCanvas);
            _panDragStartX = _canvasPanX;
            _panDragStartY = _canvasPanY;
            LatheCanvas.CaptureMouse();
            LatheCanvas.Cursor = Cursors.SizeAll;
        }

        private void LatheCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isPanning) return;
            var pos = e.GetPosition(LatheCanvas);
            _canvasPanX = _panDragStartX + (pos.X - _panDragStart.X);
            _canvasPanY = _panDragStartY + (pos.Y - _panDragStart.Y);
            RenderLathe();
        }

        private void LatheCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isPanning = false;
            LatheCanvas.ReleaseMouseCapture();
            LatheCanvas.Cursor = Cursors.Arrow;
        }

        private void ResetCanvasView()
        {
            _canvasZoom = 1.0;
            _canvasPanX = 0;
            _canvasPanY = 0;
            RenderLathe();
        }

        private void ResetCanvasView_Click(object sender, RoutedEventArgs e) => ResetCanvasView();

        private void Log(string msg, string type = "normal")
        {
            Console.AppendText(msg + "\n");
            Console.CaretIndex = Console.Text.Length;
            Console.ScrollToEnd();
        }

        // ---- MDI keypad ----

        private void BuildDualKeyGrid()
        {
            foreach (var (primary, secondary) in DualKeys)
            {
                var button = new Button
                {
                    Style = (Style)FindResource("KeypadKey"),
                    Content = BuildDualKeyContent(primary, secondary),
                    Tag = (primary, secondary)
                };
                AutomationProperties.SetAutomationId(button, "Key_" + primary);
                button.Click += DualKey_Click;
                DualKeyGrid.Children.Add(button);
            }
        }

        private static StackPanel BuildDualKeyContent(string primary, string secondary)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = primary,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Brushes.White
            });
            if (!string.IsNullOrEmpty(secondary))
                panel.Children.Add(new TextBlock
                {
                    Text = secondary,
                    FontSize = 8,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = Brushes.Orange
                });
            return panel;
        }

        private void DualKey_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not (string primary, string secondary))
                return;

            var useSecondary = _shiftArmed && !string.IsNullOrEmpty(secondary);
            var chosen = useSecondary ? secondary : primary;
            _shiftArmed = false;
            UpdateShiftIndicator();

            var text = chosen switch
            {
                "SP" => " ",
                "EOB" => "\r\n",
                _ => chosen
            };

            InsertIntoFocusedInput(text);
        }

        private void ShiftKey_Click(object sender, RoutedEventArgs e)
        {
            _shiftArmed = !_shiftArmed;
            UpdateShiftIndicator();
        }

        private void UpdateShiftIndicator()
        {
            ShiftKeyButton.Background = _shiftArmed
                ? new SolidColorBrush(Color.FromRgb(0x00, 0x44, 0x00))
                : new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2a));
        }

        private void CanKey_Click(object sender, RoutedEventArgs e)
        {
            var target = _focusedInput ?? CommandLineInput;
            if (target.SelectionLength > 0)
            {
                target.Text = target.Text.Remove(target.SelectionStart, target.SelectionLength);
                target.CaretIndex = target.SelectionStart;
            }
            else if (target.CaretIndex > 0)
            {
                var idx = target.CaretIndex;
                target.Text = target.Text.Remove(idx - 1, 1);
                target.CaretIndex = idx - 1;
            }
            target.Focus();
        }

        private void DeleteKey_Click(object sender, RoutedEventArgs e)
        {
            var target = _focusedInput ?? CommandLineInput;
            if (target.SelectionLength > 0)
            {
                target.Text = target.Text.Remove(target.SelectionStart, target.SelectionLength);
                target.CaretIndex = target.SelectionStart;
            }
            else if (target.CaretIndex < target.Text.Length)
            {
                target.Text = target.Text.Remove(target.CaretIndex, 1);
            }
            target.Focus();
        }

        private void InputKey_Click(object sender, RoutedEventArgs e)
        {
            var target = _focusedInput ?? CommandLineInput;
            if (target == CommandLineInput)
                RunMdi_Click(sender, e);
            else
                InsertIntoFocusedInput("\r\n");
        }

        private void PageUp_Click(object sender, RoutedEventArgs e)
        {
            if (HelpScreen.Visibility == Visibility.Visible)
                HelpScrollViewer.PageUp();
        }

        private void PageDown_Click(object sender, RoutedEventArgs e)
        {
            if (HelpScreen.Visibility == Visibility.Visible)
                HelpScrollViewer.PageDown();
        }

        private void ArrowLeft_Click(object sender, RoutedEventArgs e) => MoveCaret(-1);
        private void ArrowRight_Click(object sender, RoutedEventArgs e) => MoveCaret(1);

        private void MoveCaret(int delta)
        {
            var target = _focusedInput ?? CommandLineInput;
            target.CaretIndex = Math.Clamp(target.CaretIndex + delta, 0, target.Text.Length);
            target.Focus();
        }

        private void ArrowUp_Click(object sender, RoutedEventArgs e)
        {
            if (_focusedInput == GCodeInput)
                GCodeInput.LineUp();
        }

        private void ArrowDown_Click(object sender, RoutedEventArgs e)
        {
            if (_focusedInput == GCodeInput)
                GCodeInput.LineDown();
        }

        private void Input_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
                _focusedInput = tb;
        }

        private void InsertIntoFocusedInput(string text)
        {
            var target = _focusedInput ?? CommandLineInput;
            var start = target.SelectionStart;
            var selLen = target.SelectionLength;
            target.Text = target.Text.Remove(start, selLen).Insert(start, text);
            target.CaretIndex = start + text.Length;
            target.Focus();
        }
    }
}
