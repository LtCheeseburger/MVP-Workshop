using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MVPWorkshop.App;

public sealed class Conv : IValueConverter
{
    private readonly Func<object?, object?> _f;
    private Conv(Func<object?, object?> f) => _f = f;

    public static readonly Conv BoolToVis = new(v => v is true ? Visibility.Visible : Visibility.Collapsed);
    public static readonly Conv LevelBrush = new(v => Application.Current.Resources[(v as string) switch
    {
        "ERROR" => "Bad", "WARN" => "Accent", _ => "Info"
    }] as Brush);

    public static readonly Conv KindBrush = new(v => Application.Current.Resources[v as string == "SAVED" ? "Good" : "Accent"] as Brush);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => _f(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
    public static Style Style(string key) => (Style)Application.Current.Resources[key];
}
