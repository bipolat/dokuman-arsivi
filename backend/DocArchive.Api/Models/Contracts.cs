namespace DocArchive.Api.Models;

/// <summary>Legacy tablodaki bir satırın uygulama içi karşılığı. Index bunları RAM'de tutar.</summary>
public sealed class DocumentEntry
{
    public required long Id { get; init; }
    public required string FileName { get; init; }
    public required string DocumentType { get; init; }
    public required string Department { get; init; }
    public required string UploadedBy { get; init; }
    public required long SizeBytes { get; init; }
    public required DateTime CreatedAt { get; init; }
    public string? StoragePath { get; init; }

    /// <summary>
    /// Dosya gerçekten diskte var mı. İndeks kurulurken bir kez ölçülüyor: arayüzde
    /// "tıklanabilir ama 404 veren" bağlantı üretmemek için yolun dolu olması yeterli değil.
    /// </summary>
    public bool FileAvailable { get; init; }

    /// <summary>Snippet göstermek için kalıcı tutulan baş kısım (~800 karakter).</summary>
    public string SnippetSource { get; init; } = string.Empty;

    /// <summary>
    /// Aranabilir tam metin. Yalnızca indeksleme anında kullanılır, hemen ardından null'a
    /// çekilir; aksi halde her dokümanın tam metni RAM'de kalıcı olarak dururdu.
    /// </summary>
    public string? IndexableContent { get; set; }

    /// <summary>Sidecar'dan gelen içerik hash'i. Legacy tabloya kolon eklemediğimiz için ayrı tutuluyor.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Dosya adı imzası - "benzer isimli" duplicate tespiti için.</summary>
    public string[] NameSignature { get; init; } = [];

    /// <summary>BM25 için doküman uzunluğu (ağırlıklı token sayısı).</summary>
    public double Length { get; set; }
}

public sealed record DocumentDto(
    long Id,
    string FileName,
    string DocumentType,
    string Department,
    string UploadedBy,
    long SizeBytes,
    DateTime CreatedAt,
    string? Sha256,
    string? Snippet,
    double Score,
    int DuplicateCount,
    IReadOnlyList<DuplicateRef> Duplicates,
    bool ContentIndexed,
    string ContentNote,
    bool FileAvailable);

public sealed record DuplicateRef(
    long Id,
    string FileName,
    string Department,
    string UploadedBy,
    DateTime CreatedAt,
    string Reason);

public sealed record FacetBucket(string Key, int Count);

public sealed record FacetsDto(
    IReadOnlyList<FacetBucket> Types,
    IReadOnlyList<FacetBucket> Departments);

/// <summary>Kullanıcıya "boş sonuç" yerine yapılabilir bir sonraki adım göstermek için.</summary>
public sealed record Suggestion(string Kind, string Label, string? Query = null, string? Type = null, string? Department = null);

public sealed record SearchResponse(
    IReadOnlyList<DocumentDto> Items,
    int Total,
    int Page,
    int PageSize,
    double TookMs,
    FacetsDto Facets,
    IReadOnlyList<string> Messages,
    IReadOnlyList<Suggestion> Suggestions,
    string? DidYouMean,
    int CollapsedDuplicates,
    int MatchesIgnoringFilters);

public sealed record SearchQuery
{
    public string? Q { get; init; }
    public string? Type { get; init; }
    public string? Department { get; init; }
    public DateTime? From { get; init; }
    public DateTime? To { get; init; }
    public string? Sort { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 20;
    public bool CollapseDuplicates { get; init; } = true;
}

public sealed record PrecheckRequest(string FileName, long SizeBytes, string? Sha256);

public sealed record PrecheckResponse(
    string Verdict,
    string Message,
    IReadOnlyList<DocumentDto> ExactMatches,
    IReadOnlyList<DocumentDto> SimilarMatches);

public sealed record UploadResponse(
    string Verdict,
    string Message,
    DocumentDto? Document,
    IReadOnlyList<DocumentDto> ExactMatches,
    IReadOnlyList<DocumentDto> SimilarMatches);

public sealed record MetaResponse(
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Departments,
    IReadOnlyList<string> Users);

public sealed record ReindexResponse(
    int Scanned,
    int Extracted,
    int NoTextLayer,
    int Unsupported,
    int MissingFile,
    long TookMs,
    IReadOnlyList<string> Samples);

public sealed record InsightsResponse(
    int DocumentCount,
    int ContentIndexedCount,
    int ContentMissingCount,
    int TermCount,
    int DuplicateClusters,
    int DuplicateDocuments,
    long WastedBytes,
    DateTime IndexBuiltAt,
    long IndexBuildMs,
    IReadOnlyList<FacetBucket> TopZeroResultQueries,
    IReadOnlyList<FacetBucket> TopDuplicateClusters);
