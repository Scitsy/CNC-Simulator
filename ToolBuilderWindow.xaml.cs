using System;
using System.Linq;
using System.Windows;

namespace FanucSimulator
{
    public partial class ToolBuilderWindow : Window
    {
        private readonly OffsetTables _offsets;

        // Fired after a successful Apply, so MainWindow can refresh its grids/canvas and persist -
        // this window never touches MainWindow's UI directly, only the shared OffsetTables/ToolCatalog data.
        public event Action? ToolApplied;

        // Live-preview inputs get re-rendered on every keystroke, which means they're regularly
        // blank or mid-edit garbage - each numeric field falls back to its last successfully
        // parsed value instead of throwing or snapping to zero.
        private double _lastNoseRadius = 0.4;
        private double _lastWidth = 0;
        private double _lastShankSize = 25;
        private double _lastOverhang = 40;
        private double _lastInsertReach = 6;

        private bool _initialized;

        public ToolBuilderWindow(OffsetTables offsets)
        {
            InitializeComponent();
            _offsets = offsets;

            TypeCombo.ItemsSource = Enum.GetValues(typeof(ToolType));
            InsertCombo.ItemsSource = Enum.GetValues(typeof(InsertShape));
            GrooveReferenceCombo.ItemsSource = Enum.GetValues(typeof(GrooveReference));
            ApplyToolNumberCombo.ItemsSource = Enumerable.Range(1, 8).ToList();

            TypeCombo.SelectedItem = ToolType.OdTurning;
            InsertCombo.SelectedItem = InsertShape.Diamond80;
            GrooveReferenceCombo.SelectedItem = GrooveReference.Center;
            ApplyToolNumberCombo.SelectedIndex = 0;

            _initialized = true;

            Loaded += (_, _) => UpdatePreview();
        }

        private void TypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (TypeCombo.SelectedItem is ToolType type)
            {
                var geometryHasInsert = type == ToolType.OdTurning || type == ToolType.IdBoring;
                InsertCombo.IsEnabled = geometryHasInsert;
                if (!geometryHasInsert)
                    InsertCombo.SelectedItem = InsertShape.None;
                else if (InsertCombo.SelectedItem is InsertShape.None or null)
                    InsertCombo.SelectedItem = InsertShape.Diamond80;

                var isGrooveOrThread = type == ToolType.Grooving || type == ToolType.IdGrooving || type == ToolType.Threading;
                GrooveReferenceCombo.IsEnabled = isGrooveOrThread;
                if (!isGrooveOrThread)
                    GrooveReferenceCombo.SelectedItem = GrooveReference.Center;
            }
            UpdatePreview();
        }

        private void Field_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

        private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreview();

        private void UpdatePreview()
        {
            if (!_initialized)
                return;

            var type = TypeCombo.SelectedItem is ToolType t ? t : ToolType.Undefined;
            var insert = InsertCombo.SelectedItem is InsertShape i ? i : InsertShape.None;

            if (double.TryParse(NoseRadiusBox.Text, out var nr) && nr >= 0)
                _lastNoseRadius = nr;
            if (double.TryParse(WidthBox.Text, out var w) && w >= 0)
                _lastWidth = w;
            if (double.TryParse(ShankSizeBox.Text, out var ss) && ss > 0)
                _lastShankSize = ss;
            if (double.TryParse(OverhangBox.Text, out var oh) && oh > 0)
                _lastOverhang = oh;
            if (double.TryParse(InsertReachBox.Text, out var ir) && ir > 0)
                _lastInsertReach = ir;

            var reference = GrooveReferenceCombo.SelectedItem is GrooveReference r ? r : GrooveReference.Center;

            var p = new ToolGeometryParams(type, insert, _lastNoseRadius, _lastWidth, _lastShankSize, _lastOverhang, reference, _lastInsertReach);
            ToolGeometryRenderer.Render(PreviewCanvas, p);
        }

        private void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (ApplyToolNumberCombo.SelectedItem is not int toolNumber)
            {
                MessageBox.Show(this, "Select a tool number to apply to first.", "No Tool Selected", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var entry = new CatalogEntry
            {
                InsertDesignation = InsertDesignationBox.Text.Trim(),
                HolderDesignation = HolderDesignationBox.Text.Trim(),
                Description = DescriptionBox.Text.Trim(),
                Type = TypeCombo.SelectedItem is ToolType type ? type : ToolType.Undefined,
                Insert = InsertCombo.SelectedItem is InsertShape insert ? insert : InsertShape.None,
                NoseRadius = _lastNoseRadius,
                Width = _lastWidth,
                ShankSize = _lastShankSize,
                Overhang = _lastOverhang,
                Reference = GrooveReferenceCombo.SelectedItem is GrooveReference reference ? reference : GrooveReference.Center,
                InsertReach = _lastInsertReach
            };

            _offsets.GetOrCreateTool(toolNumber).AssignFromCatalog(entry);

            if (AddToSessionCatalogCheck.IsChecked == true)
                ToolCatalog.Entries.Add(entry);

            ToolApplied?.Invoke();

            MessageBox.Show(this, $"Applied to T{toolNumber:D2}.", "Tool Builder", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
