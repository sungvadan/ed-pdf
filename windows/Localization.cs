using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace PdfSvd;

/// Translated UI text, shared with the Mac app: the French source text is the key, translations come from
/// Resources/Localizations/<lang>.lproj/Localizable.strings (embedded). Values use `%d`/`%@` like on the Mac.
/// Checked by scripts/l10n.py, which also scans the `Tr("…")` calls of this project.
public static partial class L10n
{
    public static readonly string[] Languages = ["fr", "en", "de", "es", "it", "pt-BR", "ja", "zh-Hans"];
    static Dictionary<string, string> table = [];

    public static string Language { get; private set; } = "fr";

    /// Loads the translations for the UI language of Windows (French when not available).
    public static void Load(CultureInfo culture)
    {
        Language = Match(culture);
        table = Language == "fr" ? [] : Read($"{Language}.lproj");
    }

    static string Match(CultureInfo culture)
    {
        var name = culture.Name;
        if (name.StartsWith("zh")) return "zh-Hans";
        if (name.StartsWith("pt")) return "pt-BR";
        var language = culture.TwoLetterISOLanguageName;
        return Array.IndexOf(Languages, language) >= 0 ? language : "en";
    }

    static Dictionary<string, string> Read(string lproj)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.Replace('\\', '/').Equals($"l10n/{lproj}/Localizable.strings", StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var entries = new Dictionary<string, string>();
            foreach (Match m in Entry().Matches(reader.ReadToEnd()))
                entries[Unescape(m.Groups[1].Value)] = Unescape(m.Groups[2].Value);
            return entries;
        }
        return [];
    }

    [GeneratedRegex("""^\s*"((?:[^"\\]|\\.)*)"\s*=\s*"((?:[^"\\]|\\.)*)"\s*;\s*$""", RegexOptions.Multiline)]
    private static partial Regex Entry();

    static string Unescape(string s) => s.Replace("\\\"", "\"").Replace("\\n", "\n").Replace("\\\\", "\\");

    /// The translation of `key`, with `%d`, `%@` (and `%1$@`…) replaced by `arguments`.
    public static string Tr(string key, params object[] arguments)
    {
        var format = table.GetValueOrDefault(key, key);
        if (arguments.Length == 0) return format.Replace("%%", "%");
        var next = 0;
        return Specifier().Replace(format, m =>
        {
            if (m.Value == "%%") return "%";
            var index = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) - 1 : next++;
            return index < arguments.Length ? Convert.ToString(arguments[index], CultureInfo.CurrentCulture) ?? "" : "";
        });
    }

    [GeneratedRegex(@"%%|%(?:(\d+)\$)?(?:ll|l)?[d@]")]
    private static partial Regex Specifier();
}
