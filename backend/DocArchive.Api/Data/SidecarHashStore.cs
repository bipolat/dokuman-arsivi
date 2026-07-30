using System.Security.Cryptography;
using System.Text.Json;

namespace DocArchive.Api.Data;

/// <summary>
/// Doküman -> içerik hash'i eşlemesi. Legacy tabloya kolon ekleyemediğimiz için
/// append-only bir JSONL dosyasında yaşıyor (yeni altyapı yok, yeni servis yok).
///
/// Kabul edilen risk: bu dosya uygulama sunucusunda duruyor. Kaybolursa hash'ler
/// dosya içeriklerinden yeniden üretilebilir (backfill), yani kalıcı veri kaybı değil;
/// ama çok sunuculu kuruluma geçildiğinde paylaşımlı bir yere taşınması gerekir.
/// </summary>
public sealed class SidecarHashStore
{
    private readonly string _path;
    private readonly Dictionary<long, string> _hashes = [];
    private readonly Lock _writeLock = new();

    private sealed record Record(long DocumentId, string Sha256, string AddedAt);

    public SidecarHashStore(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Load();
    }

    public int Count => _hashes.Count;

    private void Load()
    {
        if (!File.Exists(_path)) return;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var record = JsonSerializer.Deserialize<Record>(line);
                if (record is not null) _hashes[record.DocumentId] = record.Sha256;
            }
            catch (JsonException)
            {
                // Bozuk tek satır tüm dosyayı kullanılamaz hale getirmesin: atla.
            }
        }
    }

    public string? Get(long documentId) => _hashes.GetValueOrDefault(documentId);

    public void Set(long documentId, string sha256)
    {
        lock (_writeLock)
        {
            _hashes[documentId] = sha256;
            var line = JsonSerializer.Serialize(new Record(documentId, sha256, DateTime.UtcNow.ToString("O")));
            File.AppendAllText(_path, line + Environment.NewLine);
        }
    }

    public static string ComputeSha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));
}
