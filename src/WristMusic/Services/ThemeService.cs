using System.Windows;

namespace WristMusic.Services;

public static class ThemeService
{
    public static bool IsDark { get; private set; }
    public static void Apply(bool dark)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var palette = dictionaries.FirstOrDefault(d => d.Source?.OriginalString is string source &&
            (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) || source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase)));
        var next = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{(dark ? "Dark" : "Light")}.xaml") };
        if (palette is not null) dictionaries[dictionaries.IndexOf(palette)] = next;
        else dictionaries.Insert(0, next);
        IsDark = dark;
    }
}
