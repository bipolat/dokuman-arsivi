using System.IO.Compression;
using System.Text;

namespace DocArchive.Api.Data;

/// <summary>
/// SADECE DEMO. Örnek dokümanlar için uzantısıyla gerçekten uyumlu dosyalar üretir:
/// açılabilen PDF, Word'de açılan DOCX, Excel'de açılan XLSX vb.
///
/// Neden gerekli: "dokümana tıklayınca açılsın" ve "içerik araması" özelliklerinin ikisi de
/// diskte gerçek dosya olmasını gerektiriyor. Sahte yol tutan demo verisiyle ikisi de
/// gösterilemez. Ayrıca üretilen bu dosyalar, içerik çıkarıcının gerçek testi oluyor.
/// </summary>
public static class DemoFileFactory
{
    public static byte[] Create(string fileName, string title, string body)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => CreatePdf(title, body, withTextLayer: true),
            ".docx" => CreateOoxmlWord(title, body),
            ".xlsx" => CreateOoxmlSheet(title, body),
            ".odt" => CreateOpenDocument(title, body),
            ".rtf" => CreateRtf(title, body),
            ".html" or ".htm" => CreateHtml(title, body),
            ".doc" => CreateLegacyDocPlaceholder(title, body),
            _ => new UTF8Encoding(false).GetBytes($"{title}\r\n\r\n{body}\r\n"),
        };
    }

    /// <summary>Metin katmanı olmayan PDF: taranmış belge senaryosu (OCR gerektirir).</summary>
    public static byte[] CreateScannedPdf(string title) => CreatePdf(title, string.Empty, withTextLayer: false);

    // -------------------------------------------------------------------- PDF

    /// <summary>
    /// Elle yazılmış minimal PDF 1.4. Sıkıştırma yok, xref tablosu doğru byte
    /// offset'leriyle üretiliyor; hem tarayıcılar hem PdfPig sorunsuz açıyor.
    /// Helvetica/WinAnsiEncoding Türkçe'nin tamamını taşımadığı için gövde metni
    /// ASCII'ye katlanıyor - arama tarafında sorun değil, sorgular da katlanıyor.
    /// </summary>
    private static byte[] CreatePdf(string title, string body, bool withTextLayer)
    {
        var content = withTextLayer
            ? BuildTextStream(title, body)
            // Taranmış belge taklidi: sadece gri bir dikdörtgen, hiç metin operatörü yok.
            : "0.85 0.85 0.85 rg\n60 80 475 700 re f\n";

        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] " +
            "/Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length {content.Length} >>\nstream\n{content}endstream",
        };

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];

        for (var i = 0; i < objects.Length; i++)
        {
            offsets[i] = builder.Length;
            builder.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        var xrefOffset = builder.Length;
        builder.Append("xref\n0 ").Append(objects.Length + 1).Append('\n');
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
            builder.Append(offset.ToString("D10")).Append(" 00000 n \n");

        builder.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        builder.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF");

        // Tamamı ASCII: karakter sayısı = byte sayısı, dolayısıyla offset'ler geçerli.
        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static string BuildTextStream(string title, string body)
    {
        var stream = new StringBuilder("BT\n/F1 13 Tf\n50 780 Td\n16 TL\n");
        stream.Append('(').Append(EscapePdf(Fold(title))).Append(") Tj T*\nT*\n");
        stream.Append("/F1 10 Tf\n13 TL\n");

        foreach (var line in Wrap(Fold(body), 92))
            stream.Append('(').Append(EscapePdf(line)).Append(") Tj T*\n");

        stream.Append("ET\n");
        return stream.ToString();
    }

    private static string EscapePdf(string text) =>
        text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static readonly Dictionary<char, string> AsciiFold = new()
    {
        ['ı'] = "i", ['İ'] = "I", ['ş'] = "s", ['Ş'] = "S", ['ğ'] = "g", ['Ğ'] = "G",
        ['ü'] = "u", ['Ü'] = "U", ['ö'] = "o", ['Ö'] = "O", ['ç'] = "c", ['Ç'] = "C",
    };

    private static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (AsciiFold.TryGetValue(c, out var replacement)) builder.Append(replacement);
            else if (c < 128) builder.Append(c);
            else builder.Append('?');
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------ OOXML / ODF

    private static byte[] CreateOoxmlWord(string title, string body)
    {
        var paragraphs = new StringBuilder();
        foreach (var text in Paragraphs(title, body))
            paragraphs.Append($"<w:p><w:r><w:t xml:space=\"preserve\">{Xml(text)}</w:t></w:r></w:p>");

        return Zip(new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
                </Types>
                """,
            ["_rels/.rels"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
                </Relationships>
                """,
            ["word/document.xml"] =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">" +
                $"<w:body>{paragraphs}</w:body></w:document>",
        });
    }

    private static byte[] CreateOoxmlSheet(string title, string body)
    {
        var rows = new StringBuilder();
        var rowIndex = 1;
        foreach (var text in Paragraphs(title, body))
        {
            rows.Append($"<row r=\"{rowIndex}\"><c r=\"A{rowIndex}\" t=\"inlineStr\">" +
                        $"<is><t xml:space=\"preserve\">{Xml(text)}</t></is></c></row>");
            rowIndex++;
        }

        return Zip(new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                  <Default Extension="xml" ContentType="application/xml"/>
                  <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                  <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                </Types>
                """,
            ["_rels/.rels"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                </Relationships>
                """,
            ["xl/workbook.xml"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets><sheet name="Sayfa1" sheetId="1" r:id="rId1"/></sheets>
                </workbook>
                """,
            ["xl/_rels/workbook.xml.rels"] = """
                <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                </Relationships>
                """,
            ["xl/worksheets/sheet1.xml"] =
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                $"<sheetData>{rows}</sheetData></worksheet>",
        });
    }

    private static byte[] CreateOpenDocument(string title, string body)
    {
        var paragraphs = new StringBuilder();
        foreach (var text in Paragraphs(title, body))
            paragraphs.Append($"<text:p>{Xml(text)}</text:p>");

        return Zip(new Dictionary<string, string>
        {
            ["mimetype"] = "application/vnd.oasis.opendocument.text",
            ["META-INF/manifest.xml"] = """
                <?xml version="1.0" encoding="UTF-8"?>
                <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
                  <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.text"/>
                  <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
                </manifest:manifest>
                """,
            ["content.xml"] =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" " +
                "xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" office:version=\"1.2\">" +
                $"<office:body><office:text>{paragraphs}</office:text></office:body></office:document-content>",
        });
    }

    private static byte[] Zip(Dictionary<string, string> entries)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return buffer.ToArray();
    }

    // ----------------------------------------------------------- diğer biçimler

    private static byte[] CreateRtf(string title, string body)
    {
        var builder = new StringBuilder(@"{\rtf1\ansi\ansicpg1254\deff0{\fonttbl{\f0 Arial;}}");
        builder.Append(@"\f0\fs28\b ").Append(RtfEscape(title)).Append(@"\b0\fs22\par\par ");
        foreach (var paragraph in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            builder.Append(RtfEscape(paragraph.Trim())).Append(@"\par ");
        builder.Append('}');
        return Encoding.GetEncoding(1254).GetBytes(builder.ToString());
    }

    private static string RtfEscape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '\\' or '{' or '}') builder.Append('\\').Append(c);
            else if (c < 128) builder.Append(c);
            else builder.Append(@"\u").Append((int)c).Append('?');
        }
        return builder.ToString();
    }

    private static byte[] CreateHtml(string title, string body)
    {
        var paragraphs = string.Join("\n", body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => $"  <p>{Xml(p.Trim())}</p>"));

        // $$ ile: tek süslü parantez düz metin (CSS), interpolasyon {{...}} ile yapılır.
        var html = $$"""
            <!doctype html>
            <html lang="tr">
            <head><meta charset="utf-8"><title>{{Xml(title)}}</title>
            <style>body{font-family:sans-serif;max-width:40rem;margin:2rem auto}</style></head>
            <body>
              <h1>{{Xml(title)}}</h1>
            {{paragraphs}}
            </body>
            </html>
            """;
        return new UTF8Encoding(false).GetBytes(html);
    }

    /// <summary>
    /// Eski ikili .doc üretmiyoruz (OLE2 yazmak bu prototipin kapsamı dışı).
    /// Uzantısı .doc olan düz metin dosyası: içerik çıkarıcının "desteklenmeyen biçim"
    /// yolunu dürüstçe göstermeye yarıyor.
    /// </summary>
    private static byte[] CreateLegacyDocPlaceholder(string title, string body) =>
        new UTF8Encoding(false).GetBytes($"{title}\r\n\r\n{body}\r\n");

    private static IEnumerable<string> Paragraphs(string title, string body)
    {
        yield return title;
        foreach (var paragraph in body.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            yield return paragraph.Trim();
    }

    private static string Xml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
