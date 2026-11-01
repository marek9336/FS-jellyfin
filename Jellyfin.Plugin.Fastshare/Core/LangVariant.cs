using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Fastshare.Core;

/// <summary>
/// Jazyková verze seriálu: „CZ" (dabing), „EN+CZtit" (anglický zvuk s českými titulky). Seriál jich může chtít víc
/// (Super drbna: CZ i EN+CZtit) — chybějící jazyková verze dílu se pak nabízí zvlášť. Čistý C#.
/// </summary>
public static class LangVariant
{
    private static readonly Regex Format = new(@"^(?<a>[A-Z]{2})(\+(?<s>[A-Z]{2})tit)?$", RegexOptions.Compiled);

    public static bool IsValid(string? v) => v != null && Format.IsMatch(v);

    /// <summary>„EN+CZtit" → (EN, CZ), „CZ" → (CZ, null).</summary>
    public static (string Audio, string? Subs) Parse(string v)
    {
        var m = Format.Match(v ?? string.Empty);
        return m.Success ? (m.Groups["a"].Value, m.Groups["s"].Success ? m.Groups["s"].Value : null) : ((v ?? "CZ").ToUpperInvariant(), null);
    }

    /// <summary>„EN + CZ titulky".</summary>
    public static string Label(string v)
    {
        var (a, s) = Parse(v);
        return s == null ? a : $"{a} + {s} titulky";
    }

    /// <summary>Kód jazyka z Jellyfinu (ISO 639-2 „cze"/„ces", „eng"…) nebo z názvu → CZ / SK / EN / …; neznámý → null.</summary>
    public static string? Normalize(string? lang)
    {
        var l = (lang ?? string.Empty).Trim().ToLowerInvariant();
        return l switch
        {
            "" or "und" or "unk" or "mis" or "zxx" => null,
            "cze" or "ces" or "cs" or "cz" or "czech" or "čeština" => "CZ",
            "slo" or "slk" or "sk" or "slovak" => "SK",
            "eng" or "en" or "english" => "EN",
            "ger" or "deu" or "de" => "DE",
            "fre" or "fra" or "fr" => "FR",
            "pol" or "pl" => "PL",
            "rus" or "ru" => "RU",
            "jpn" or "ja" or "jp" => "JA",
            _ => l.Length is 2 or 3 ? l[..2].ToUpperInvariant() : null,
        };
    }

    /// <summary>
    /// Odpovídá soubor jazykové verzi? Zvuk musí obsahovat jazyk verze (u souboru bez údaje o jazyku se bere
    /// výchozí jazyk z Nastavení — jinak by každý starý soubor „chyběl"), titulky jsou-li požadované.
    /// </summary>
    public static bool Matches(LibraryVersion v, string variant, string defaultLang)
    {
        var (a, s) = Parse(variant);
        var audioOk = v.AudioLangs.Count == 0
            ? string.Equals(a, Normalize(defaultLang) ?? defaultLang, StringComparison.OrdinalIgnoreCase) && s == null
            : v.AudioLangs.Contains(a, StringComparer.OrdinalIgnoreCase);
        return audioOk && (s == null || v.SubLangs.Contains(s, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Volby výběru pro jazykovou verzi: jen soubory s tím zvukem (a titulky), ostatní preference z Nastavení.</summary>
    public static FileSelectorOptions Options(FileSelectorOptions o, string variant, bool primary)
    {
        var (a, s) = Parse(variant);
        o.Lang1 = a;
        o.Lang2 = string.Empty;
        o.Lang3 = string.Empty;
        o.SkipWithoutPreferredLang = true;
        o.PreferSubsWhenForeign = false;
        o.RequireSubsLang = s;
        if (!primary)
        {
            o.UnknownLangScore = 0; // u druhé jazykové verze soubor bez údaje o jazyku nebrat (byl by to nejspíš dabing)
        }

        return o;
    }
}
