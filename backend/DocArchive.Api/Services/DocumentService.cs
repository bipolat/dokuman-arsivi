using System.Diagnostics;
using DocArchive.Api.Data;
using DocArchive.Api.Models;
using DocArchive.Api.Search;

namespace DocArchive.Api.Services;

public sealed class DocumentService(
    LegacyDocumentRepository repository,
    SidecarHashStore sidecar,
    DocumentIndex index,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<DocumentService> logger)
{
    public string DataDirectory { get; } = Path.Combine(
        environment.ContentRootPath, configuration["Storage:DataDir"] ?? "storage");

    public string BlobDirectory => Path.Combine(DataDirectory, "blobs");

    /// <summary>Uygulama açılışında indeksi legacy tablodan kurar.</summary>
    public void LoadIndex()
    {
        var rows = repository.ReadAll();
        var entries = rows.Select(row => LegacyDocumentRepository.ToEntry(row, sidecar.Get(row.Id)));
        index.Build(entries);
        logger.LogInformation("İndeks kuruldu: {Count} doküman, {Ms} ms.", rows.Count, index.BuildMs);
    }

    public PrecheckResponse Precheck(PrecheckRequest request)
    {
        List<DocumentEntry> exact = string.IsNullOrWhiteSpace(request.Sha256)
            ? []
            : index.FindByHash(request.Sha256.Trim().ToLowerInvariant());
        var similar = index.FindSimilar(request.FileName, request.SizeBytes, request.Sha256);

        if (exact.Count > 0)
        {
            var first = exact[0];
            return new PrecheckResponse(
                "duplicate",
                $"Bu dosyanın birebir aynısı sistemde var: \"{first.FileName}\" — {first.Department}, {first.UploadedBy}, {first.CreatedAt:dd.MM.yyyy}.",
                exact.Select(index.ToDto).ToList(),
                similar.Select(index.ToDto).ToList());
        }

        if (similar.Count > 0)
        {
            return new PrecheckResponse(
                "similar",
                $"Benzer isimli {similar.Count} doküman bulundu. Yüklemeden önce kontrol etmek ister misiniz?",
                [],
                similar.Select(index.ToDto).ToList());
        }

        return new PrecheckResponse("new", "Bu dosya sistemde görünmüyor, yükleyebilirsiniz.", [], []);
    }

    public async Task<(int StatusCode, UploadResponse Body)> UploadAsync(
        Stream content, string fileName, string documentType, string department, string uploadedBy,
        bool force, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        // İstemcinin gönderdiği hash'e asla güvenilmez; sunucu yeniden hesaplar.
        // İstemci hash'i sadece "yüklemeden önce uyar" deneyimi için kullanılıyor.
        var sha256 = SidecarHashStore.ComputeSha256(bytes);

        var exact = index.FindByHash(sha256);
        if (exact.Count > 0 && !force)
        {
            var first = exact[0];
            return (409, new UploadResponse(
                "duplicate",
                $"Yükleme durduruldu: aynı içerik zaten kayıtlı — \"{first.FileName}\" ({first.Department}, {first.CreatedAt:dd.MM.yyyy}). Mevcut dokümanı kullanabilirsiniz.",
                null,
                exact.Select(index.ToDto).ToList(),
                []));
        }

        var similar = index.FindSimilar(fileName, bytes.Length, sha256);
        if (similar.Count > 0 && !force)
        {
            return (409, new UploadResponse(
                "similar",
                $"Benzer isimli {similar.Count} doküman var. Aynı dokümanın yeni sürümü değilse \"yine de yükle\" ile devam edin.",
                null,
                [],
                similar.Select(index.ToDto).ToList()));
        }

        // İçerik adresli depolama: aynı içerik ikinci kez diske yazılmaz.
        Directory.CreateDirectory(BlobDirectory);
        var blobPath = Path.Combine(BlobDirectory, sha256 + SafeExtension(fileName));
        if (!File.Exists(blobPath))
            await File.WriteAllBytesAsync(blobPath, bytes, cancellationToken);

        var extraction = ContentExtractor.Extract(fileName, bytes);
        var createdAt = DateTime.UtcNow;
        var id = repository.Insert(fileName, documentType, department, uploadedBy, bytes.Length, createdAt, blobPath, extraction.Text);
        sidecar.Set(id, sha256);

        var entry = LegacyDocumentRepository.ToEntry(
            new LegacyDocumentRepository.Row(id, fileName, documentType, department, uploadedBy, bytes.Length, createdAt, blobPath, extraction.Text),
            sha256);
        index.Add(entry);

        var message = exact.Count > 0
            ? "Yüklendi (kopya olduğu bilinerek onaylandı). Aynı içerik daha önce de kayıtlıydı."
            : "Doküman yüklendi ve aramaya eklendi.";

        // İçerik çıkarımının sonucunu kullanıcıya söylüyoruz: "yükledim ama içinde arayamıyorum"
        // sürprizini sonradan yaşamasın.
        message += extraction.Status == ExtractionStatus.Extracted
            ? $" İçerik indekslendi ({extraction.Detail})."
            : $" İçerik aranamıyor: {extraction.Detail}";

        logger.LogInformation("Upload: {Id} {FileName} force={Force} dupes={Dupes} extraction={Status}",
            id, fileName, force, exact.Count, extraction.Status);
        return (201, new UploadResponse("created", message, index.ToDto(entry), [], []));
    }

    /// <summary>
    /// Mevcut arşivin içeriklerini geriye dönük aranabilir hale getirir: her doküman için
    /// blob'u okur, metni çıkarır ve legacy tablodaki BOŞ ContentText hücrelerini doldurur.
    ///
    /// Gerçek sistemde bu bir defalık / gece çalışan bir iş olur; burada endpoint olarak
    /// açık, çünkü prototipte etkisinin canlı görülmesi gerekiyor.
    /// </summary>
    public ReindexResponse ReindexContent()
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = repository.ReadAll();
        int extracted = 0, noTextLayer = 0, unsupported = 0, missingFile = 0;
        var samples = new List<string>();

        foreach (var row in rows)
        {
            if (!string.IsNullOrWhiteSpace(row.ContentText)) continue; // dolu hücreye dokunmuyoruz

            var path = ResolveBlobPath(row.StoragePath);
            if (path is null)
            {
                missingFile++;
                continue;
            }

            var result = ContentExtractor.Extract(row.FileName, File.ReadAllBytes(path));
            switch (result.Status)
            {
                case ExtractionStatus.Extracted:
                    repository.FillEmptyContentText(row.Id, result.Text);
                    extracted++;
                    if (samples.Count < 5) samples.Add($"{row.FileName}: {result.Detail}");
                    break;
                case ExtractionStatus.NoTextLayer:
                    noTextLayer++;
                    if (samples.Count < 5) samples.Add($"{row.FileName}: {result.Detail}");
                    break;
                default:
                    unsupported++;
                    if (samples.Count < 5) samples.Add($"{row.FileName}: {result.Detail}");
                    break;
            }
        }

        // İndeksi tamamen yeniden kur: kısmi güncelleme yapmak, dokümanı silip yeniden
        // eklemeyi gerektirir ve ters indekste tombstone yönetimi açar. Bu iş nadir
        // çalıştığı için tam yeniden kurulum bilinçli olarak daha basit seçenek.
        LoadIndex();
        stopwatch.Stop();

        logger.LogInformation("İçerik yeniden indeksleme: {Extracted} çıkarıldı, {NoText} metin katmanı yok, {Unsupported} desteklenmiyor, {Missing} dosya yok.",
            extracted, noTextLayer, unsupported, missingFile);

        return new ReindexResponse(rows.Count, extracted, noTextLayer, unsupported, missingFile,
            stopwatch.ElapsedMilliseconds, samples);
    }

    /// <summary>
    /// Dokümanın diskteki dosyasını çözer. Yol veritabanından geldiği için, blob kökünün
    /// dışına çıkan her yolu reddediyoruz - aksi halde tabloya yazılan bir yol dosya
    /// sistemini okumaya açık hale gelirdi.
    /// </summary>
    public (string Path, string ContentType, string FileName)? ResolveFile(long id)
    {
        if (!index.TryGet(id, out var entry)) return null;

        var path = ResolveBlobPath(entry.StoragePath);
        return path is null ? null : (path, GuessContentType(entry.FileName), entry.FileName);
    }

    private string? ResolveBlobPath(string? storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath)) return null;

        var root = Path.GetFullPath(DataDirectory);
        var full = Path.GetFullPath(Path.IsPathRooted(storagePath)
            ? storagePath
            : Path.Combine(environment.ContentRootPath, storagePath));

        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? full : null;
    }

    /// <summary>Uzantıyı dosya sistemi için güvenli hale getirir (yol ayırıcı vb. içermesin).</summary>
    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Length is 0 or > 12) return string.Empty;
        return extension.All(c => char.IsLetterOrDigit(c) || c == '.') ? extension.ToLowerInvariant() : string.Empty;
    }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".xml"] = "application/xml; charset=utf-8",
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".rtf"] = "application/rtf",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text",
        [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
    };

    private static string GuessContentType(string fileName) =>
        ContentTypes.GetValueOrDefault(Path.GetExtension(fileName), "application/octet-stream");

    public DocumentDto? GetById(long id) =>
        index.TryGet(id, out var entry) ? index.ToDto(entry) : null;

    public IReadOnlyList<DocumentDto> GetDuplicates(long id)
    {
        if (!index.TryGet(id, out var entry)) return [];

        List<DocumentEntry> exact = string.IsNullOrEmpty(entry.Sha256)
            ? []
            : index.FindByHash(entry.Sha256).Where(d => d.Id != id).ToList();
        var similar = index.FindSimilar(entry.FileName, entry.SizeBytes, entry.Sha256)
            .Where(d => d.Id != id && exact.All(e => e.Id != d.Id));

        return exact.Concat(similar).Select(index.ToDto).ToList();
    }
}
