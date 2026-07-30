namespace DocArchive.Api.Search;

/// <summary>
/// Türkçe metinleri arama için sadeleştirir.
/// Neden: kullanıcı "sozlesme" yazıp "Sözleşme" bulamadığında bunu "arama sonuçları karışık"
/// diye raporluyor. Tek bir katlama (folding) kuralı bu şikayetin büyük kısmını kapatıyor.
/// </summary>
public static class TextNormalizer
{
    // Türkçe karakterler + sık kullanılan aksanlar ASCII'ye katlanır.
    private static readonly Dictionary<char, char> Fold = new()
    {
        ['ı'] = 'i', ['İ'] = 'i', ['I'] = 'i', ['i'] = 'i',
        ['ş'] = 's', ['Ş'] = 's',
        ['ğ'] = 'g', ['Ğ'] = 'g',
        ['ü'] = 'u', ['Ü'] = 'u',
        ['ö'] = 'o', ['Ö'] = 'o',
        ['ç'] = 'c', ['Ç'] = 'c',
        ['â'] = 'a', ['Â'] = 'a',
        ['î'] = 'i', ['Î'] = 'i',
        ['û'] = 'u', ['Û'] = 'u',
    };

    // Bilinçli olarak kısa tutuldu. Uzun stopword listesi arama kalitesini
    // ölçmeden büyütmek, ölçemediğimiz bir riski büyütmek olur.
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "ve", "ile", "icin", "bir", "bu", "da", "de", "ki", "mi", "veya",
        "the", "and", "for", "pdf", "docx", "xlsx", "doc", "copy", "kopya"
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = Fold.TryGetValue(c, out var folded) ? folded : char.ToLowerInvariant(c);
        }
        return new string(buffer);
    }

    /// <summary>Normalize eder ve harf/rakam dışındaki her şeyden böler.</summary>
    public static List<string> Tokenize(string? value, bool dropStopWords = true)
    {
        var tokens = new List<string>();
        if (string.IsNullOrEmpty(value)) return tokens;

        var normalized = Normalize(value);
        var start = -1;
        for (var i = 0; i <= normalized.Length; i++)
        {
            var isWord = i < normalized.Length && char.IsLetterOrDigit(normalized[i]);
            if (isWord)
            {
                if (start < 0) start = i;
                continue;
            }

            if (start >= 0)
            {
                var token = normalized[start..i];
                start = -1;
                if (token.Length < 2) continue;
                if (dropStopWords && StopWords.Contains(token)) continue;
                tokens.Add(token);
            }
        }
        return tokens;
    }

    /// <summary>
    /// Dosya adını "aynı dokümanın başka bir kopyası mı?" karşılaştırması için sadeleştirir.
    /// "Teklif_ACME_final_v2 (1).pdf" -> "acme teklif"
    /// </summary>
    private static readonly HashSet<string> VersionNoise = new(StringComparer.Ordinal)
    {
        "final", "son", "yeni", "new", "revize", "rev", "guncel", "guncellenmis",
        "v1", "v2", "v3", "v4", "v5", "draft", "taslak", "imzali", "signed", "1", "2", "3"
    };

    public static string[] NameSignature(string fileName)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(fileName ?? string.Empty);
        return Tokenize(withoutExtension)
            .Where(t => !VersionNoise.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>Jaccard benzerliği: iki dosya adı imzasının örtüşme oranı.</summary>
    public static double SignatureSimilarity(string[] left, string[] right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        var intersection = left.Intersect(right, StringComparer.Ordinal).Count();
        var union = left.Length + right.Length - intersection;
        return union == 0 ? 0 : (double)intersection / union;
    }

    /// <summary>Sıfır sonuçlu aramalarda "şunu mu demek istediniz?" için kullanılıyor.</summary>
    public static int EditDistance(string a, string b, int max)
    {
        if (Math.Abs(a.Length - b.Length) > max) return max + 1;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var rowMin = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                rowMin = Math.Min(rowMin, current[j]);
            }
            if (rowMin > max) return max + 1;
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}
