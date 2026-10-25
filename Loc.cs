using System.Globalization;
using System.Windows;

namespace ScanDesk;

/// <summary>
/// UI language. XAML uses {DynamicResource key} so text switches live; code uses <see cref="T"/> / <see cref="F"/>.
/// The strings live in Strings\nl.xaml and Strings\en.xaml.
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "nl";

    public static readonly (string Code, string Name)[] Languages = [("nl", "Nederlands"), ("en", "English")];

    // A plain copy of the current strings, so background threads can read them safely.
    static Dictionary<string, string> _strings = [];
    static ResourceDictionary? _current;

    public static string Language { get; private set; } = DefaultLanguage;

    public static void Apply(string? code)
    {
        if (!Languages.Any(l => l.Code == code)) code = DefaultLanguage;
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Strings/{code}.xaml") };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current != null) merged.Remove(_current);
        merged.Add(dict);
        _current = dict;
        _strings = dict.Keys.Cast<string>().ToDictionary(k => k, k => dict[k] as string ?? k);
        Language = code!;

        var culture = CultureInfo.GetCultureInfo(code == "nl" ? "nl-NL" : "en-US");
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
    }

    /// <summary>Text for <paramref name="key"/> in the current language.</summary>
    public static string T(string key) => _strings.TryGetValue(key, out var s) ? s : key;

    /// <summary>Formatted text, e.g. F("PageOf", 2, 5) → "Pagina 2 van 5".</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);
}
