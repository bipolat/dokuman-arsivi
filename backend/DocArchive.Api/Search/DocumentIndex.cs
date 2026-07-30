using System.Collections.Concurrent;
using System.Diagnostics;
using DocArchive.Api.Models;
using DocArchive.Api.Services;

namespace DocArchive.Api.Search;

/// <summary>
/// RAM içi ters indeks (inverted index) + BM25 sıralama.
///
/// Neden böyle: veritabanı değiştirilemiyor ve 3 ay içinde ek altyapı (Elasticsearch vb.)
/// alınmayacak. Arama yükünü mevcut DB'nin üstünden alıp uygulama sürecine taşımak,
/// yeni bir bileşen eklemeden 400ms bütçesini korumanın en kısa yolu.
///
/// Eş zamanlılık: okuma çok, yazma az (günde birkaç bin upload). Bu yüzden
/// ReaderWriterLockSlim; sorgular paralel çalışır, upload'lar mikrosaniyeler boyunca bloklar.
/// </summary>
public sealed class DocumentIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private const int MaxPrefixExpansion = 200;

    private readonly record struct Posting(long DocId, float Weight);

    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<long, DocumentEntry> _byId = [];
    private readonly List<DocumentEntry> _docs = [];
    private readonly Dictionary<string, List<Posting>> _inverted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<long>> _byHash = new(StringComparer.Ordinal);
    private double _totalLength;

    // Ölçmediğimiz şeyi iyileştiremeyiz: sonuç dönmeyen aramaları sayıyoruz.
    private readonly ConcurrentDictionary<string, int> _zeroResultQueries = new(StringComparer.Ordinal);

    public DateTime BuiltAt { get; private set; }
    public long BuildMs { get; private set; }

    // Alan ağırlıkları: dosya adı eşleşmesi, içerik eşleşmesinden daha güçlü bir sinyaldir.
    private static readonly (Func<DocumentEntry, string?> Selector, float Weight)[] Fields =
    [
        (d => d.FileName, 3.0f),
        (d => d.DocumentType, 2.0f),
        (d => d.Department, 1.5f),
        (d => d.UploadedBy, 1.0f),
        // Tam metin: snippet için tutulan baş kısım değil, çıkarılan içeriğin tamamı.
        (d => d.IndexableContent ?? d.SnippetSource, 1.0f),
    ];

    public void Build(IEnumerable<DocumentEntry> entries)
    {
        var sw = Stopwatch.StartNew();
        _lock.EnterWriteLock();
        try
        {
            _byId.Clear();
            _docs.Clear();
            _inverted.Clear();
            _byHash.Clear();
            _totalLength = 0;
            foreach (var entry in entries) AddUnsafe(entry);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
        sw.Stop();
        BuiltAt = DateTime.UtcNow;
        BuildMs = sw.ElapsedMilliseconds;
    }

    public void Add(DocumentEntry entry)
    {
        _lock.EnterWriteLock();
        try { AddUnsafe(entry); }
        finally { _lock.ExitWriteLock(); }
    }

    private void AddUnsafe(DocumentEntry entry)
    {
        if (_byId.ContainsKey(entry.Id)) return;

        var weights = new Dictionary<string, float>(StringComparer.Ordinal);
        double length = 0;
        foreach (var (selector, weight) in Fields)
        {
            foreach (var token in TextNormalizer.Tokenize(selector(entry)))
            {
                weights[token] = weights.GetValueOrDefault(token) + weight;
                length += weight;
            }
        }

        entry.Length = length;
        // Token'lar çıkarıldı; tam metni RAM'de tutmanın bir faydası kalmadı.
        entry.IndexableContent = null;
        _byId[entry.Id] = entry;
        _docs.Add(entry);
        _totalLength += length;

        foreach (var (token, weight) in weights)
        {
            if (!_inverted.TryGetValue(token, out var postings))
                _inverted[token] = postings = new List<Posting>(1);
            postings.Add(new Posting(entry.Id, weight));
        }

        if (!string.IsNullOrEmpty(entry.Sha256))
        {
            if (!_byHash.TryGetValue(entry.Sha256, out var group))
                _byHash[entry.Sha256] = group = [];
            group.Add(entry.Id);
        }
    }

    public bool TryGet(long id, out DocumentEntry entry)
    {
        _lock.EnterReadLock();
        try { return _byId.TryGetValue(id, out entry!); }
        finally { _lock.ExitReadLock(); }
    }

    // ---------------------------------------------------------------- arama

    public SearchResponse Search(SearchQuery query)
    {
        var sw = Stopwatch.StartNew();
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var tokens = TextNormalizer.Tokenize(query.Q);
        var messages = new List<string>();
        var suggestions = new List<Suggestion>();
        string? didYouMean = null;

        _lock.EnterReadLock();
        try
        {
            var n = _docs.Count;
            var avgLength = n == 0 ? 1 : _totalLength / n;

            // 1) Sorgu eşleşmesi
            IReadOnlyDictionary<long, double> matched;
            if (tokens.Count == 0)
            {
                matched = _docs.ToDictionary(d => d.Id, _ => 0d);
            }
            else
            {
                // Varsayılan AND: "arama sonuçları çok karışık" şikayetinin ana sebebi
                // OR mantığının alakasız dokümanları listeye sokması.
                var expandLast = !string.IsNullOrEmpty(query.Q) && !char.IsWhiteSpace(query.Q[^1]);
                var perToken = new List<Dictionary<long, double>>(tokens.Count);
                for (var i = 0; i < tokens.Count; i++)
                {
                    var isLast = i == tokens.Count - 1;
                    perToken.Add(ScoreToken(tokens[i], n, avgLength, isLast && expandLast));
                }

                var andMatch = Combine(perToken, requireAll: true);
                if (andMatch.Count == 0 && tokens.Count > 1)
                {
                    // Sessizce boş dönmek yerine kısmi eşleşme + açık uyarı.
                    andMatch = Combine(perToken, requireAll: false);
                    if (andMatch.Count > 0)
                        messages.Add("Tüm kelimeleri içeren doküman yok; kısmi eşleşmeler gösteriliyor.");
                }
                matched = andMatch;

                if (matched.Count == 0)
                    didYouMean = FindDidYouMean(tokens);
            }

            var matchesIgnoringFilters = matched.Count;

            // 2) Filtreler
            var filtered = new List<(DocumentEntry Doc, double Score)>();
            var typeFacet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var deptFacet = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var (id, score) in matched)
            {
                var doc = _byId[id];
                if (query.From.HasValue && doc.CreatedAt < query.From.Value) continue;
                if (query.To.HasValue && doc.CreatedAt > query.To.Value) continue;

                // Facet'ler tür/departman filtresi UYGULANMADAN sayılır; böylece
                // "Finans'ta 3 sonuç var" gibi eyleme dönüşebilir öneri üretebiliyoruz.
                typeFacet[doc.DocumentType] = typeFacet.GetValueOrDefault(doc.DocumentType) + 1;
                deptFacet[doc.Department] = deptFacet.GetValueOrDefault(doc.Department) + 1;

                if (!string.IsNullOrEmpty(query.Type) && !doc.DocumentType.Equals(query.Type, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(query.Department) && !doc.Department.Equals(query.Department, StringComparison.OrdinalIgnoreCase)) continue;

                filtered.Add((doc, Boost(score, doc)));
            }

            // 3) Aynı içerikli kopyaları tek satırda topla
            var collapsed = 0;
            List<(DocumentEntry Doc, double Score, List<DocumentEntry> Dupes)> rows;
            if (query.CollapseDuplicates)
            {
                rows = CollapseByHash(filtered, out collapsed);
            }
            else
            {
                rows = filtered.Select(f => (f.Doc, f.Score, new List<DocumentEntry>())).ToList();
            }

            // 4) Sıralama
            var sort = string.IsNullOrWhiteSpace(query.Sort)
                ? (tokens.Count == 0 ? "newest" : "relevance")
                : query.Sort.ToLowerInvariant();
            rows = sort switch
            {
                "newest" => rows.OrderByDescending(r => r.Doc.CreatedAt).ToList(),
                "oldest" => rows.OrderBy(r => r.Doc.CreatedAt).ToList(),
                "name" => rows.OrderBy(r => r.Doc.FileName, StringComparer.CurrentCulture).ToList(),
                "size" => rows.OrderByDescending(r => r.Doc.SizeBytes).ToList(),
                _ => rows.OrderByDescending(r => r.Score).ThenByDescending(r => r.Doc.CreatedAt).ToList(),
            };

            var total = rows.Count;
            var items = rows
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(r => ToDto(r.Doc, r.Score, r.Dupes, tokens))
                .ToList();

            // 5) Anlamlı geri bildirim
            BuildFeedback(query, tokens, total, matchesIgnoringFilters, collapsed, typeFacet, deptFacet, messages, suggestions);

            // Sadece gerçekten hiç eşleşme olmayan sorguları sayıyoruz. Filtre yüzünden
            // boşalan sonuçlar "arama bulamadı" sinyali değil, kullanıcının kendi daraltması.
            if (matchesIgnoringFilters == 0 && tokens.Count > 0)
            {
                var key = string.Join(' ', tokens);
                _zeroResultQueries.AddOrUpdate(key, 1, (_, c) => c + 1);
            }

            sw.Stop();
            return new SearchResponse(
                items,
                total,
                page,
                pageSize,
                // Alt-milisaniye sürüyor; tam sayıya yuvarlamak "0 ms" gibi bozuk görünüyor.
                Math.Round(sw.Elapsed.TotalMilliseconds, 2),
                new FacetsDto(TopBuckets(typeFacet, 10), TopBuckets(deptFacet, 10)),
                messages,
                suggestions,
                didYouMean,
                collapsed,
                matchesIgnoringFilters);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private Dictionary<long, double> ScoreToken(string token, int n, double avgLength, bool allowPrefix)
    {
        var scores = new Dictionary<long, double>();
        Accumulate(token, 1.0, scores, n, avgLength);

        if (allowPrefix && token.Length >= 2)
        {
            // Yazarken arama: son kelime ön-ek olarak da genişletilir.
            var expanded = 0;
            foreach (var term in _inverted.Keys)
            {
                if (term.Length <= token.Length || !term.StartsWith(token, StringComparison.Ordinal)) continue;
                Accumulate(term, 0.6, scores, n, avgLength);
                if (++expanded >= MaxPrefixExpansion) break;
            }
        }
        return scores;
    }

    private void Accumulate(string term, double factor, Dictionary<long, double> scores, int n, double avgLength)
    {
        if (!_inverted.TryGetValue(term, out var postings)) return;

        var df = postings.Count;
        var idf = Math.Log(1 + (n - df + 0.5) / (df + 0.5));
        foreach (var posting in postings)
        {
            var doc = _byId[posting.DocId];
            var tf = posting.Weight;
            var score = idf * (tf * (K1 + 1)) / (tf + K1 * (1 - B + B * doc.Length / avgLength)) * factor;
            // Aynı token grubunda en iyi eşleşmeyi al; ön-ek genişlemesi puanı şişirmesin.
            if (!scores.TryGetValue(posting.DocId, out var existing) || score > existing)
                scores[posting.DocId] = score;
        }
    }

    private static Dictionary<long, double> Combine(List<Dictionary<long, double>> perToken, bool requireAll)
    {
        var result = new Dictionary<long, double>();
        if (perToken.Count == 0) return result;

        if (!requireAll)
        {
            foreach (var scores in perToken)
                foreach (var (id, score) in scores)
                    result[id] = result.GetValueOrDefault(id) + score;
            return result;
        }

        var smallest = perToken.OrderBy(p => p.Count).First();
        foreach (var (id, _) in smallest)
        {
            double sum = 0;
            var inAll = true;
            foreach (var scores in perToken)
            {
                if (!scores.TryGetValue(id, out var score)) { inAll = false; break; }
                sum += score;
            }
            if (inAll) result[id] = sum;
        }
        return result;
    }

    /// <summary>Yeni dokümanlar hafif öne çıkar: kullanıcılar çoğunlukla son işi arıyor.</summary>
    private static double Boost(double score, DocumentEntry doc)
    {
        var ageDays = Math.Max(0, (DateTime.UtcNow - doc.CreatedAt).TotalDays);
        return score * (1 + 0.2 * Math.Exp(-ageDays / 365.0));
    }

    private List<(DocumentEntry Doc, double Score, List<DocumentEntry> Dupes)> CollapseByHash(
        List<(DocumentEntry Doc, double Score)> filtered, out int collapsed)
    {
        collapsed = 0;
        var rows = new List<(DocumentEntry, double, List<DocumentEntry>)>();
        var groups = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var (doc, score) in filtered.OrderBy(f => f.Doc.CreatedAt))
        {
            if (string.IsNullOrEmpty(doc.Sha256))
            {
                rows.Add((doc, score, []));
                continue;
            }

            if (groups.TryGetValue(doc.Sha256, out var index))
            {
                var row = rows[index];
                row.Item3.Add(doc);
                // Kopya grubu, grup içindeki en yüksek puanı temsil eder.
                if (score > row.Item2) rows[index] = (row.Item1, score, row.Item3);
                collapsed++;
                continue;
            }

            groups[doc.Sha256] = rows.Count;
            rows.Add((doc, score, []));
        }
        return rows;
    }

    private static void BuildFeedback(
        SearchQuery query, List<string> tokens, int total, int matchesIgnoringFilters, int collapsed,
        Dictionary<string, int> typeFacet, Dictionary<string, int> deptFacet,
        List<string> messages, List<Suggestion> suggestions)
    {
        var hasFilter = !string.IsNullOrEmpty(query.Type) || !string.IsNullOrEmpty(query.Department)
                        || query.From.HasValue || query.To.HasValue;

        if (total == 0)
        {
            if (matchesIgnoringFilters > 0 && hasFilter)
            {
                messages.Add($"Bu filtrelerle sonuç yok, ancak aynı arama filtresiz {matchesIgnoringFilters} sonuç veriyor.");
                suggestions.Add(new Suggestion("clear-filters", "Filtreleri temizle"));

                if (!string.IsNullOrEmpty(query.Type))
                {
                    foreach (var bucket in typeFacet.OrderByDescending(b => b.Value).Take(2))
                        suggestions.Add(new Suggestion("switch-type", $"Tür: {bucket.Key} ({bucket.Value})", Type: bucket.Key));
                }
                if (!string.IsNullOrEmpty(query.Department))
                {
                    foreach (var bucket in deptFacet.OrderByDescending(b => b.Value).Take(2))
                        suggestions.Add(new Suggestion("switch-department", $"Departman: {bucket.Key} ({bucket.Value})", Department: bucket.Key));
                }
            }
            else if (tokens.Count > 1)
            {
                messages.Add("Hiç sonuç yok. Aramayı kısaltmak genelde işe yarar.");
                suggestions.Add(new Suggestion("shorten-query", $"Sadece \"{tokens[0]}\" ile ara", Query: tokens[0]));
            }
            else
            {
                messages.Add("Hiç sonuç yok. Doküman adı, departman veya yükleyen kişiyle de arayabilirsiniz.");
            }
            return;
        }

        if (collapsed > 0)
            messages.Add($"{collapsed} adet birebir aynı kopya, ilk yüklenen sürümün altında gruplandı.");

        if (total > 50 && tokens.Count <= 1 && !hasFilter)
        {
            messages.Add($"{total} sonuç var. Daraltmak için tür veya departman seçin.");
            foreach (var bucket in typeFacet.OrderByDescending(b => b.Value).Take(3))
                suggestions.Add(new Suggestion("narrow-type", $"{bucket.Key} ({bucket.Value})", Type: bucket.Key));
        }
    }

    private string? FindDidYouMean(List<string> tokens)
    {
        // Sadece sıfır sonuçta çalışır; sıcak yolda maliyet yok.
        var target = tokens.OrderByDescending(t => t.Length).First();
        string? best = null;
        var bestDistance = int.MaxValue;
        var bestDf = 0;

        foreach (var (term, postings) in _inverted)
        {
            var distance = TextNormalizer.EditDistance(target, term, 2);
            if (distance > 2) continue;
            if (distance < bestDistance || (distance == bestDistance && postings.Count > bestDf))
            {
                best = term;
                bestDistance = distance;
                bestDf = postings.Count;
            }
        }

        if (best is null) return null;
        return string.Join(' ', tokens.Select(t => t == target ? best : t));
    }

    private DocumentDto ToDto(DocumentEntry doc, double score, List<DocumentEntry> dupes, List<string> tokens)
    {
        var duplicates = dupes
            .Select(d => new DuplicateRef(d.Id, d.FileName, d.Department, d.UploadedBy, d.CreatedAt, "Birebir aynı içerik"))
            .ToList();

        var contentIndexed = doc.SnippetSource.Length > 0;
        return new DocumentDto(
            doc.Id, doc.FileName, doc.DocumentType, doc.Department, doc.UploadedBy,
            doc.SizeBytes, doc.CreatedAt, doc.Sha256,
            BuildSnippet(doc.SnippetSource, tokens),
            Math.Round(score, 4),
            duplicates.Count,
            duplicates,
            contentIndexed,
            DescribeContent(doc.FileName, contentIndexed),
            doc.FileAvailable);
    }

    /// <summary>
    /// Kullanıcıya "bu dokümanın içinde neden arama yapılamıyor" sorusunun cevabı.
    /// Sessizce eksik davranmak yerine sebebi söylemek, arayüzde tek satırlık bir etiket
    /// karşılığında çok fazla kafa karışıklığını önlüyor.
    /// </summary>
    private static string DescribeContent(string fileName, bool contentIndexed)
    {
        if (contentIndexed) return "İçerik aranabilir.";
        if (!ContentExtractor.IsSupported(fileName))
            return $"{Path.GetExtension(fileName)} biçiminden metin çıkarılamıyor; yalnızca dosya adı ve etiketleriyle bulunur.";
        return "İçerik henüz çıkarılmadı (metin katmanı olmayan taranmış belge olabilir).";
    }

    public DocumentDto ToDto(DocumentEntry doc) => ToDto(doc, 0, [], []);

    private static string? BuildSnippet(string source, List<string> tokens)
    {
        if (string.IsNullOrEmpty(source)) return null;
        const int window = 160;

        if (tokens.Count > 0)
        {
            var normalized = TextNormalizer.Normalize(source);
            var position = -1;
            foreach (var token in tokens)
            {
                var index = normalized.IndexOf(token, StringComparison.Ordinal);
                if (index >= 0 && (position < 0 || index < position)) position = index;
            }
            if (position > window / 2)
            {
                var start = Math.Max(0, position - window / 2);
                var length = Math.Min(window, source.Length - start);
                return "…" + source.Substring(start, length).Trim() + "…";
            }
        }

        return source.Length <= window ? source : source[..window].Trim() + "…";
    }

    private static List<FacetBucket> TopBuckets(Dictionary<string, int> counts, int take) =>
        counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key)
              .Take(take)
              .Select(c => new FacetBucket(c.Key, c.Value))
              .ToList();

    // ------------------------------------------------------- duplicate tespiti

    public List<DocumentEntry> FindByHash(string sha256)
    {
        _lock.EnterReadLock();
        try
        {
            return _byHash.TryGetValue(sha256, out var ids)
                ? ids.Select(id => _byId[id]).OrderBy(d => d.CreatedAt).ToList()
                : [];
        }
        finally { _lock.ExitReadLock(); }
    }

    /// <summary>
    /// İçerik hash'i tutmayan ama "muhtemelen aynı" olan dokümanlar.
    /// Dosya adı imzası + boyut yakınlığı yeterli bir sinyal; kesin değil, o yüzden uyarı olarak sunuluyor.
    /// </summary>
    public List<DocumentEntry> FindSimilar(string fileName, long sizeBytes, string? excludeHash, int take = 5)
    {
        var signature = TextNormalizer.NameSignature(fileName);
        if (signature.Length == 0) return [];

        _lock.EnterReadLock();
        try
        {
            var scored = new List<(DocumentEntry Doc, double Similarity)>();
            foreach (var doc in _docs)
            {
                if (excludeHash is not null && doc.Sha256 == excludeHash) continue;

                var similarity = TextNormalizer.SignatureSimilarity(signature, doc.NameSignature);
                if (similarity < 0.5) continue;

                var sizeClose = sizeBytes > 0 && doc.SizeBytes > 0 &&
                                Math.Abs(doc.SizeBytes - sizeBytes) <= Math.Max(1024, sizeBytes * 0.05);
                if (sizeClose) similarity += 0.25;

                if (similarity >= 0.6) scored.Add((doc, similarity));
            }

            return scored
                .OrderByDescending(s => s.Similarity)
                .ThenByDescending(s => s.Doc.CreatedAt)
                .Take(take)
                .Select(s => s.Doc)
                .ToList();
        }
        finally { _lock.ExitReadLock(); }
    }

    // ------------------------------------------------------------- istatistik

    public InsightsResponse GetInsights()
    {
        _lock.EnterReadLock();
        try
        {
            var clusters = _byHash.Where(g => g.Value.Count > 1).ToList();
            long wasted = 0;
            var duplicateDocs = 0;
            var topClusters = new List<FacetBucket>();

            foreach (var (_, ids) in clusters)
            {
                var docs = ids.Select(id => _byId[id]).OrderBy(d => d.CreatedAt).ToList();
                duplicateDocs += docs.Count - 1;
                wasted += docs[0].SizeBytes * (docs.Count - 1);
                topClusters.Add(new FacetBucket(docs[0].FileName, docs.Count));
            }

            var contentIndexed = _docs.Count(d => d.SnippetSource.Length > 0);

            return new InsightsResponse(
                _docs.Count,
                contentIndexed,
                _docs.Count - contentIndexed,
                _inverted.Count,
                clusters.Count,
                duplicateDocs,
                wasted,
                BuiltAt,
                BuildMs,
                _zeroResultQueries.OrderByDescending(q => q.Value).Take(10)
                    .Select(q => new FacetBucket(q.Key, q.Value)).ToList(),
                topClusters.OrderByDescending(c => c.Count).Take(10).ToList());
        }
        finally { _lock.ExitReadLock(); }
    }

    public MetaResponse GetMeta()
    {
        _lock.EnterReadLock();
        try
        {
            return new MetaResponse(
                _docs.Select(d => d.DocumentType).Distinct().OrderBy(t => t).ToList(),
                _docs.Select(d => d.Department).Distinct().OrderBy(t => t).ToList(),
                _docs.Select(d => d.UploadedBy).Distinct().OrderBy(t => t).ToList());
        }
        finally { _lock.ExitReadLock(); }
    }
}
