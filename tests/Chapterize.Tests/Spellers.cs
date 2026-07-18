// Chapterize - mark chapter starts in audiobooks using Whisper speech recognition
// Copyright (c) 2026 Jan O. Gretza. Written with Claude (Anthropic).
// MIT license - see the LICENSE file in the repository root.

namespace Chapterize.Tests;

/// <summary>
/// Reference number-to-words spellers, implemented independently from the parsers under
/// test so that the exhaustive round-trip tests actually cross-check two implementations
/// against each other instead of testing a parser against itself.
/// </summary>
public static class Spellers
{
    /// <summary>Spells 0-999 in English ("three hundred and twenty-one").</summary>
    /// <param name="n">Number to spell.</param>
    /// <param name="useAnd">Insert "and" after "hundred" (British style).</param>
    /// <param name="hyphen">Join tens and units with a hyphen instead of a space.</param>
    public static string English(int n, bool useAnd, bool hyphen)
    {
        string[] units =
        [
            "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
            "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen",
            "seventeen", "eighteen", "nineteen",
        ];
        string[] tens =
        [
            "", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety",
        ];

        string Sub100(int m) => m < 20
            ? units[m]
            : tens[m / 10] + (m % 10 > 0 ? (hyphen ? "-" : " ") + units[m % 10] : "");

        if (n < 100)
            return Sub100(n);
        var rest = n % 100;
        return units[n / 100] + " hundred" + (rest > 0 ? (useAnd ? " and " : " ") + Sub100(rest) : "");
    }

    /// <summary>Spells 0-999 in German ("dreihunderteinundzwanzig").</summary>
    /// <param name="n">Number to spell.</param>
    /// <param name="einhundert">Spell 1xx as "einhundert..." instead of plain "hundert...".</param>
    public static string German(int n, bool einhundert)
    {
        string[] simple =
        [
            "null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun",
            "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn",
            "siebzehn", "achtzehn", "neunzehn",
        ];
        string[] unitComp =
            ["", "ein", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun"];
        string[] tens =
        [
            "", "", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig",
            "achtzig", "neunzig",
        ];

        string Sub100(int m)
        {
            if (m < 20)
                return simple[m];
            var u = m % 10;
            return (u > 0 ? unitComp[u] + "und" : "") + tens[m / 10];
        }

        if (n < 100)
            return Sub100(n);
        var h = n / 100;
        var rest = n % 100;
        var prefix = h == 1 ? (einhundert ? "ein" : "") : unitComp[h];
        return prefix + "hundert" + (rest > 0 ? Sub100(rest) : "");
    }

    /// <summary>Spells 0-999 in Dutch ("driehonderdeenentwintig", with proper trema forms).</summary>
    public static string Dutch(int n)
    {
        string[] simple =
        [
            "nul", "een", "twee", "drie", "vier", "vijf", "zes", "zeven", "acht", "negen",
            "tien", "elf", "twaalf", "dertien", "veertien", "vijftien", "zestien",
            "zeventien", "achttien", "negentien",
        ];
        string[] tens =
        [
            "", "", "twintig", "dertig", "veertig", "vijftig", "zestig", "zeventig",
            "tachtig", "negentig",
        ];

        string Sub100(int m)
        {
            if (m < 20)
                return simple[m];
            var u = m % 10;
            if (u == 0)
                return tens[m / 10];
            // After a unit ending in "e" the connector takes a trema: "tweeëntwintig".
            var connector = simple[u].EndsWith('e') ? "ën" : "en";
            return simple[u] + connector + tens[m / 10];
        }

        if (n < 100)
            return Sub100(n);
        var h = n / 100;
        var rest = n % 100;
        return (h == 1 ? "" : simple[h]) + "honderd" + (rest > 0 ? Sub100(rest) : "");
    }

    /// <summary>Spells 0-999 in French ("deux cent quatre-vingt-onze", "soixante et onze").</summary>
    public static string French(int n)
    {
        string[] simple =
        [
            "zéro", "un", "deux", "trois", "quatre", "cinq", "six", "sept", "huit", "neuf",
            "dix", "onze", "douze", "treize", "quatorze", "quinze", "seize",
            "dix-sept", "dix-huit", "dix-neuf",
        ];
        string[] tens = ["", "", "vingt", "trente", "quarante", "cinquante", "soixante"];

        string Sub100(int m)
        {
            if (m < 20)
                return simple[m];
            if (m < 70)
            {
                var u = m % 10;
                if (u == 0) return tens[m / 10];
                if (u == 1) return tens[m / 10] + " et un";
                return tens[m / 10] + "-" + simple[u];
            }
            if (m == 71)
                return "soixante et onze";
            if (m < 80)
                return "soixante-" + simple[m - 60];
            if (m == 80)
                return "quatre-vingts";
            return "quatre-vingt-" + simple[m - 80];
        }

        if (n < 100)
            return Sub100(n);
        var h = n / 100;
        var rest = n % 100;
        var head = h == 1 ? "cent" : simple[h] + " cent" + (rest == 0 ? "s" : "");
        return head + (rest > 0 ? " " + Sub100(rest) : "");
    }

    /// <summary>Spells 0-999 in Italian ("trecentoventuno", with proper vowel elision).</summary>
    /// <param name="n">Number to spell.</param>
    /// <param name="elideCento">Also elide after the hundreds: "centotto" instead of "centootto".</param>
    public static string Italian(int n, bool elideCento)
    {
        string[] simple =
        [
            "zero", "uno", "due", "tre", "quattro", "cinque", "sei", "sette", "otto", "nove",
            "dieci", "undici", "dodici", "tredici", "quattordici", "quindici", "sedici",
            "diciassette", "diciotto", "diciannove",
        ];
        string[] tens =
        [
            "", "", "venti", "trenta", "quaranta", "cinquanta", "sessanta", "settanta",
            "ottanta", "novanta",
        ];

        string Sub100(int m)
        {
            if (m < 20)
                return m == 3 ? "tre" : simple[m];
            var u = m % 10;
            var t = tens[m / 10];
            return u switch
            {
                0 => t,
                1 => t[..^1] + "uno",   // mandatory elision: "ventuno"
                3 => t + "tré",         // final "tre" takes the accent: "ventitré"
                8 => t[..^1] + "otto",  // mandatory elision: "ventotto"
                _ => t + simple[u],
            };
        }

        if (n < 100)
            return Sub100(n);
        var h = n / 100;
        var rest = n % 100;
        var head = h == 1 ? "cento" : simple[h] + "cento";
        if (rest == 0)
            return head;
        var tail = Sub100(rest);
        if (elideCento && (tail.StartsWith('o') || tail.StartsWith('u')))
            head = head[..^1]; // optional elision: "centotto", "duecentuno", "centottanta"
        return head + tail;
    }

    /// <summary>Spells 0-999 in Turkish ("dokuz yüz doksan dokuz", "yirmi bir").</summary>
    public static string Turkish(int n)
    {
        string[] units =
            ["sıfır", "bir", "iki", "üç", "dört", "beş", "altı", "yedi", "sekiz", "dokuz"];
        string[] tens =
            ["", "on", "yirmi", "otuz", "kırk", "elli", "altmış", "yetmiş", "seksen", "doksan"];

        if (n == 0)
            return "sıfır";
        var parts = new List<string>();
        var h = n / 100;
        if (h > 1)
            parts.Add(units[h]);
        if (h >= 1)
            parts.Add("yüz");
        var t = n / 10 % 10;
        if (t > 0)
            parts.Add(tens[t]);
        var u = n % 10;
        if (u > 0)
            parts.Add(units[u]);
        return string.Join(' ', parts);
    }

    /// <summary>Spells 0-999 in Spanish ("novecientos noventa y nueve", "veintiuno").</summary>
    public static string Spanish(int n)
    {
        string[] small =
        [
            "cero", "uno", "dos", "tres", "cuatro", "cinco", "seis", "siete", "ocho", "nueve",
            "diez", "once", "doce", "trece", "catorce", "quince", "dieciséis", "diecisiete",
            "dieciocho", "diecinueve", "veinte", "veintiuno", "veintidós", "veintitrés",
            "veinticuatro", "veinticinco", "veintiséis", "veintisiete", "veintiocho",
            "veintinueve",
        ];
        string[] tens =
        [
            "", "", "", "treinta", "cuarenta", "cincuenta", "sesenta", "setenta",
            "ochenta", "noventa",
        ];
        string[] hundreds =
        [
            "", "ciento", "doscientos", "trescientos", "cuatrocientos", "quinientos",
            "seiscientos", "setecientos", "ochocientos", "novecientos",
        ];

        string Sub100(int m)
        {
            if (m < 30)
                return small[m];
            var u = m % 10;
            return tens[m / 10] + (u > 0 ? " y " + small[u] : "");
        }

        if (n < 100)
            return Sub100(n);
        if (n == 100)
            return "cien";
        var rest = n % 100;
        return hundreds[n / 100] + (rest > 0 ? " " + Sub100(rest) : "");
    }

    /// <summary>Spells 1-999 as an English ordinal ("three hundred and twenty-first").</summary>
    /// <param name="n">Number to spell.</param>
    /// <param name="useAnd">Insert "and" after "hundred" (British style).</param>
    /// <param name="hyphen">Join tens and units with a hyphen instead of a space.</param>
    public static string EnglishOrdinal(int n, bool useAnd, bool hyphen)
    {
        var irregular = new Dictionary<string, string>
        {
            ["one"] = "first", ["two"] = "second", ["three"] = "third", ["five"] = "fifth",
            ["eight"] = "eighth", ["nine"] = "ninth", ["twelve"] = "twelfth",
            ["twenty"] = "twentieth", ["thirty"] = "thirtieth", ["forty"] = "fortieth",
            ["fifty"] = "fiftieth", ["sixty"] = "sixtieth", ["seventy"] = "seventieth",
            ["eighty"] = "eightieth", ["ninety"] = "ninetieth", ["hundred"] = "hundredth",
        };
        var s = English(n, useAnd, hyphen);
        var cut = s.LastIndexOfAny([' ', '-']) + 1;
        var last = s[cut..];
        return s[..cut] + (irregular.TryGetValue(last, out var o) ? o : last + "th");
    }

    /// <summary>Spells 1-999 as a German ordinal in base form ("dreihundertdreiundzwanzigste").</summary>
    /// <param name="n">Number to spell.</param>
    /// <param name="einhundert">Spell 1xx as "einhundert..." instead of plain "hundert...".</param>
    public static string GermanOrdinal(int n, bool einhundert)
    {
        var s = German(n, einhundert);
        return (n % 100) switch
        {
            1 => s[..^4] + "erste",           // "eins" -> "erste"
            3 => s[..^4] + "dritte",          // "drei" -> "dritte"
            7 => s[..^6] + "siebte",          // "sieben" -> "siebte"
            8 => s + "e",                     // "achte"
            0 or >= 20 => s + "ste",          // "zwanzigste", "hundertste"
            _ => s + "te",                    // "vierte", "siebzehnte"
        };
    }

    /// <summary>Spells 1-999 as a Dutch ordinal ("driehonderddrieëntwintigste").</summary>
    public static string DutchOrdinal(int n)
    {
        var s = Dutch(n);
        return (n % 100) switch
        {
            1 => s[..^3] + "eerste",          // "een" -> "eerste"
            3 => s[..^4] + "derde",           // "drie" -> "derde"
            8 or 0 or >= 20 => s + "ste",     // "achtste", "twintigste", "honderdste"
            _ => s + "de",                    // "tweede", "zevende"
        };
    }

    /// <summary>Spells 1-999 as a French ordinal ("premier", "vingt et unième", "centième").</summary>
    public static string FrenchOrdinal(int n)
    {
        if (n == 1)
            return "premier";
        var s = French(n);
        var cut = s.LastIndexOfAny([' ', '-']) + 1;
        var last = s[cut..] switch
        {
            "cinq" => "cinquième",
            "neuf" => "neuvième",
            "vingts" => "vingtième",   // "quatre-vingts" -> "quatre-vingtième"
            "cents" => "centième",     // "deux cents" -> "deux centième"
            var w when w.EndsWith('e') => w[..^1] + "ième",
            var w => w + "ième",
        };
        return s[..cut] + last;
    }

    /// <summary>Spells 1-999 as an Italian ordinal ("primo", "ventunesimo", "centottesimo").</summary>
    /// <param name="n">Number to spell.</param>
    /// <param name="elideCento">Also elide after the hundreds, like the cardinal speller.</param>
    public static string ItalianOrdinal(int n, bool elideCento)
    {
        string[] irregular =
        [
            "", "primo", "secondo", "terzo", "quarto", "quinto",
            "sesto", "settimo", "ottavo", "nono", "decimo",
        ];
        if (n <= 10)
            return irregular[n];
        var s = Italian(n, elideCento);
        if (s.EndsWith("tré", StringComparison.Ordinal))
            return s[..^3] + "treesimo"; // "-tré" keeps its (unaccented) final e
        if (s.EndsWith("sei", StringComparison.Ordinal))
            return s + "esimo";          // "-sei" keeps its final i
        return s[..^1] + "esimo";        // regular: drop the final vowel
    }

    /// <summary>Spells 1-999 as a Turkish ordinal ("birinci", "yirmi birinci", "yüzüncü").</summary>
    public static string TurkishOrdinal(int n)
    {
        var irregular = new Dictionary<string, string>
        {
            ["bir"] = "birinci", ["iki"] = "ikinci", ["üç"] = "üçüncü",
            ["dört"] = "dördüncü", ["beş"] = "beşinci", ["altı"] = "altıncı",
            ["yedi"] = "yedinci", ["sekiz"] = "sekizinci", ["dokuz"] = "dokuzuncu",
            ["on"] = "onuncu", ["yirmi"] = "yirminci", ["otuz"] = "otuzuncu",
            ["kırk"] = "kırkıncı", ["elli"] = "ellinci", ["altmış"] = "altmışıncı",
            ["yetmiş"] = "yetmişinci", ["seksen"] = "sekseninci",
            ["doksan"] = "doksanıncı", ["yüz"] = "yüzüncü",
        };
        var s = Turkish(n);
        var cut = s.LastIndexOf(' ') + 1;
        return s[..cut] + irregular[s[cut..]];
    }
}
