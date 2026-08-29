using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using CleanSpace.Models;

namespace CleanSpace.Services;

public sealed class RiskTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is RiskLevel.Safe ? "risk.safe" : value is RiskLevel.Caution ? "risk.caution" : "risk.blocked";
        var locale = CultureInfo.CurrentUICulture.Name.StartsWith("ko") ? LocaleCode.KoKr : LocaleCode.ZhCn;
        return Localizer.Get(key, locale);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class RiskBrushConverter : IValueConverter
{
    private static readonly Brush Safe = new SolidColorBrush(Color.FromRgb(25, 135, 84));
    private static readonly Brush Caution = new SolidColorBrush(Color.FromRgb(184, 107, 0));
    private static readonly Brush Blocked = new SolidColorBrush(Color.FromRgb(183, 42, 42));
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is RiskLevel.Safe ? Safe : value is RiskLevel.Caution ? Caution : Blocked;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class KeyTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var locale = CultureInfo.CurrentUICulture.Name.StartsWith("ko") ? LocaleCode.KoKr : LocaleCode.ZhCn;
        return Localizer.Get(value?.ToString() ?? "", locale);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
