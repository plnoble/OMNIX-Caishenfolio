using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Caishenfolio.Desktop.Wealth;

/// <summary>Urgent task cards get the danger border so a data problem never looks calm.</summary>
public sealed class UrgencyBorderConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var urgent = value is true;
        return urgent
            ? (System.Windows.Application.Current?.TryFindResource("BrushDanger") as Brush ?? Brushes.OrangeRed)
            : (System.Windows.Application.Current?.TryFindResource("BrushBorder") as Brush ?? Brushes.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
