using System.Globalization;
using System.Windows.Controls;

namespace PhotoViewer.Wpf;

public partial class MainWindow
{
    private const string RandomSeedMode = "random";
    private const string FixedSeedMode = "fixed";
    private const int DefaultFixedSeedValue = 0;

    private static bool SelectedSeedModeIsFixed(ComboBox comboBox)
        => comboBox.SelectedItem is ComboBoxItem { Tag: object tag }
            && string.Equals(
                Convert.ToString(tag, CultureInfo.InvariantCulture),
                FixedSeedMode,
                StringComparison.OrdinalIgnoreCase);

    private static void SelectSeedMode(ComboBox comboBox, bool fixedMode)
    {
        string expected = fixedMode ? FixedSeedMode : RandomSeedMode;
        foreach (object item in comboBox.Items)
        {
            if (item is ComboBoxItem { Tag: object tag }
                && string.Equals(
                    Convert.ToString(tag, CultureInfo.InvariantCulture),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private static bool TryParseFixedSeed(string value, out int seed)
        => int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out seed);

    private static string RestoreSeedValueText(bool fixedMode, int? value)
        => value is >= 0
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : fixedMode
                ? ""
                : DefaultFixedSeedValue.ToString(CultureInfo.InvariantCulture);
}
