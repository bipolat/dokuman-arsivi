using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace DocArchive.Api.Services;

public enum ExtractionStatus
{
    /// <summary>Metin çıkarıldı ve indekslendi.</summary>
    Extracted,

    /// <summary>Format destekleniyor ama dosyada metin katmanı yok (taranmış PDF, boş dosya).</summary>
    NoTextLayer,

    /// <summary>Format metin çıkarımını desteklemiyor (eski ikili Office, görüntü, arşiv).</summary>
    Unsupported,

    /// <summary>Dosya okunabildi ama çıkarım hata verdi (bozuk/şifreli dosya).</summary>
    Failed,
}

public readonly record struct ExtractionResult(string Text, ExtractionStatus Status, string Detail)
{
    public bool HasText => Text.Length > 0;
}

/// <summary>
/// Dosya içeriğinden aranabilir metin çıkarır.
///
/// Tasarım kararı: harici bir servis ya da OCR motoru yok. PDF için PdfPig (saf .NET,
/// native bağımlılık yok); Office/OpenDocument formatları ZIP+XML olduğu için
/// System.IO.Compression ile ek bağımlılık olmadan okunuyor. Taranmış PDF'ler
/// bilinçli olarak kapsam dışı - OCR ayrı bir altyapı meselesi.
/// </summary>
public static partial class ContentExtractor
{
    /// <summary>Aranabilir metin için üst sınır. RAM içi indeks büyüklüğünü sınırlayan asıl parametre.</summary>
    public const int MaxContentChars = 8000;

    private const long MaxBytes = 25 * 1024 * 1024;

    static ContentExtractor()
    {
        // Türkçe eski dosyalar sık sık Windows-1254 kodlu; .NET Core'da bu kod sayfası
        // varsayılan olarak kayıtlı değil.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly HashSet<string> PlainText = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".log", ".json", ".yml", ".yaml",
        ".ini", ".cfg", ".conf", ".sql", ".srt", ".vtt", ".eml", ".text", ".dat",
    };

    private static readonly HashSet<string> Markup = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".xhtml", ".xml", ".svg", ".rss", ".atom", ".xsl",
    };

    /// <summary>OOXML (Office 2007+) ve OpenDocument: ikisi de ZIP içinde XML.</summary>
    private static readonly HashSet<string> ZipXml = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".xltx",
        ".pptx", ".pptm", ".potx", ".odt", ".ods", ".odp", ".odg", ".odf",
    };

    private static readonly HashSet<string> LegacyOffice = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".xls", ".ppt", ".pps", ".mdb", ".wpd",
    };

    /// <summary>Kullanıcıya "neden aranamıyor" diyebilmek için, dosya açılmadan verilen ön bilgi.</summary>
    public static bool IsSupported(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return PlainText.Contains(extension) || Markup.Contains(extension)
            || ZipXml.Contains(extension) || extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase);
    }

    public static ExtractionResult Extract(string fileName, byte[] bytes)
    {
        if (bytes.LongLength > MaxBytes)
            return new ExtractionResult(string.Empty, ExtractionStatus.Unsupported, "Dosya metin çıkarımı için çok büyük.");

        var extension = Path.GetExtension(fileName);

        try
        {
            if (extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return FromPdf(bytes);

            if (ZipXml.Contains(extension))
                return FromZipXml(bytes, extension);

            if (extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase))
                return Finish(StripRtf(DecodeText(bytes)), "RTF");

            if (Markup.Contains(extension))
                return Finish(StripMarkup(DecodeText(bytes)), "İşaretleme dili");

            if (PlainText.Contains(extension))
                return Finish(Collapse(DecodeText(bytes)), "Düz metin");

            if (LegacyOffice.Contains(extension))
                return new ExtractionResult(string.Empty, ExtractionStatus.Unsupported,
                    $"{extension} eski ikili Office biçimi; metin çıkarımı için ayrı kütüphane gerekir.");

            return new ExtractionResult(string.Empty, ExtractionStatus.Unsupported,
                $"{(string.IsNullOrEmpty(extension) ? "Uzantısız dosya" : extension)} için metin çıkarımı desteklenmiyor.");
        }
        catch (Exception ex)
        {
            // Bozuk ya da şifreli dosya yüklemeyi engellememeli: dosya kaydedilir,
            // sadece içeriği aranamaz.
            return new ExtractionResult(string.Empty, ExtractionStatus.Failed,
                $"İçerik okunamadı: {ex.GetType().Name}");
        }
    }

    // ------------------------------------------------------------------- PDF

    private static ExtractionResult FromPdf(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        var builder = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            builder.Append(page.Text).Append('\n');
            if (builder.Length > MaxContentChars * 2) break;
        }

        var text = Collapse(builder.ToString());
        if (text.Length == 0)
        {
            return new ExtractionResult(string.Empty, ExtractionStatus.NoTextLayer,
                $"PDF'de metin katmanı yok ({document.NumberOfPages} sayfa) - taranmış belge olabilir, OCR gerekir.");
        }
        return Finish(text, $"PDF, {document.NumberOfPages} sayfa");
    }

    // ------------------------------------------- OOXML / OpenDocument (ZIP+XML)

    private static ExtractionResult FromZipXml(byte[] bytes, string extension)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var builder = new StringBuilder();
        var parts = 0;

        // sharedStrings, sheet'lerden önce gelsin diye sıralı geziyoruz (deterministik çıktı).
        foreach (var entry in archive.Entries.OrderBy(e => e.FullName.Replace('\\', '/'), StringComparer.Ordinal))
        {
            if (!IsTextPart(entry.FullName)) continue;

            using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8);
            builder.Append(StripMarkup(reader.ReadToEnd())).Append('\n');
            parts++;

            if (builder.Length > MaxContentChars * 2) break;
        }

        var text = Collapse(builder.ToString());
        if (text.Length == 0)
        {
            return new ExtractionResult(string.Empty, ExtractionStatus.NoTextLayer,
                $"{extension} dosyasında metin bulunamadı (yalnızca görüntü/grafik içeriyor olabilir).");
        }
        return Finish(text, $"{extension}, {parts} bölüm");
    }

    /// <summary>
    /// ZIP içinden hangi XML parçalarının okunacağı. Whitelist tutuyoruz çünkü paketin
    /// tamamını okumak stil/tema dosyalarından anlamsız token yığını üretir.
    /// </summary>
    private static bool IsTextPart(string rawPath)
    {
        // ZIP spec ayırıcı olarak '/' der, ama bazı üreticiler (ör. .NET Framework'ün
        // ZipFile.CreateFromDirectory'si Windows'ta) '\' yazıyor. Normalize etmezsek
        // o paketlerden hiç metin çıkmaz ve sebebi de görünmez.
        var path = rawPath.Replace('\\', '/');

        // OpenDocument (odt/ods/odp)
        if (path is "content.xml" or "meta.xml") return true;

        // Word
        if (path is "word/document.xml") return true;
        if (path.StartsWith("word/footnotes", StringComparison.Ordinal)) return true;
        if (path.StartsWith("word/endnotes", StringComparison.Ordinal)) return true;

        // Excel: hücre metinleri sharedStrings'te ya da satır içinde
        if (path is "xl/sharedStrings.xml") return true;
        if (path.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)) return true;

        // PowerPoint
        if (path.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && path.EndsWith(".xml", StringComparison.Ordinal)) return true;
        if (path.StartsWith("ppt/notesSlides/notesSlide", StringComparison.Ordinal)) return true;

        return false;
    }

    // -------------------------------------------------------- metin temizleme

    /// <summary>BOM'a bakar, yoksa UTF-8 dener, geçersizse Windows-1254'e düşer.</summary>
    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Geçerli UTF-8 değil: Türkçe eski dosyalarda en olası aday Windows-1254.
            return (Encoding.GetEncoding(1254) ?? Encoding.Latin1).GetString(bytes);
        }
    }

    [GeneratedRegex(@"<[^>]{0,4000}>", RegexOptions.Singleline)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex WhitespacePattern();

    private static string StripMarkup(string markup)
    {
        // script/style içerikleri arama için gürültü: önce onları tamamen atıyoruz.
        markup = Regex.Replace(markup, "<(script|style)[^>]*>.*?</\\1>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Paragraf/satır sınırlarını koru: kelimelerin birbirine yapışmasını engeller.
        markup = markup
            .Replace("</w:p>", "\n").Replace("</a:p>", "\n").Replace("</text:p>", "\n")
            .Replace("</p>", "\n").Replace("<br>", "\n").Replace("<br/>", "\n").Replace("<br />", "\n")
            .Replace("</tr>", "\n").Replace("</row>", "\n").Replace("</c>", " ").Replace("</si>", "\n")
            .Replace("<w:tab/>", " ").Replace("</td>", " ").Replace("</th>", " ");

        var text = TagPattern().Replace(markup, " ");
        return Collapse(System.Net.WebUtility.HtmlDecode(text));
    }

    /// <summary>RTF kontrol sözcüklerini ve grup işaretlerini atar.</summary>
    private static string StripRtf(string rtf)
    {
        var builder = new StringBuilder(rtf.Length);
        var skipDepth = 0;
        var depth = 0;

        for (var i = 0; i < rtf.Length; i++)
        {
            var c = rtf[i];

            if (c == '{') { depth++; continue; }
            if (c == '}')
            {
                if (skipDepth > 0 && depth <= skipDepth) skipDepth = 0;
                depth--;
                continue;
            }

            if (c == '\\')
            {
                // \'hh -> tek bayt karakter
                if (i + 3 < rtf.Length && rtf[i + 1] == '\'')
                {
                    if (byte.TryParse(rtf.AsSpan(i + 2, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
                    {
                        if (skipDepth == 0) builder.Append((char)b);
                        i += 3;
                        continue;
                    }
                }

                var start = ++i;
                while (i < rtf.Length && (char.IsLetter(rtf[i]))) i++;
                var word = rtf[start..i];
                while (i < rtf.Length && (char.IsDigit(rtf[i]) || rtf[i] == '-')) i++;
                if (i < rtf.Length && rtf[i] != ' ') i--; // ayırıcı boşluk değilse geri al

                // Bu gruplar metin değil: gömülü nesne, font tablosu, renk tablosu vb.
                if (word is "pict" or "fonttbl" or "colortbl" or "stylesheet" or "info" or "object" or "themedata")
                    skipDepth = depth;
                else if (word is "par" or "line" or "sect" or "page" && skipDepth == 0)
                    builder.Append('\n');
                else if (word == "tab" && skipDepth == 0)
                    builder.Append(' ');
                continue;
            }

            if (skipDepth == 0 && c != '\r' && c != '\n') builder.Append(c);
        }

        return Collapse(builder.ToString());
    }

    private static string Collapse(string text) =>
        WhitespacePattern().Replace(text.Replace('\t', ' ').Replace(' ', ' '), " ").Trim();

    private static ExtractionResult Finish(string text, string detail)
    {
        if (text.Length == 0)
            return new ExtractionResult(string.Empty, ExtractionStatus.NoTextLayer, $"{detail}: metin bulunamadı.");

        var trimmed = text.Length > MaxContentChars ? text[..MaxContentChars] : text;
        var note = text.Length > MaxContentChars
            ? $"{detail}; ilk {MaxContentChars} karakter indekslendi."
            : detail;
        return new ExtractionResult(trimmed, ExtractionStatus.Extracted, note);
    }
}
