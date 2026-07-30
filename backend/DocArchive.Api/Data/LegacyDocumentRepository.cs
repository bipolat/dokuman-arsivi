using DocArchive.Api.Models;
using DocArchive.Api.Search;
using Microsoft.Data.Sqlite;

namespace DocArchive.Api.Data;

/// <summary>
/// Mevcut (legacy) doküman tablosuna erişim.
///
/// Kural: bu sınıf ASLA DDL çalıştırmaz. "Şema değiştirilemez" kısıtı, tabloya kolon/indeks
/// eklemeyi de kapsıyor. SELECT ve INSERT normal uygulama davranışıdır; şema değişikliği değildir.
/// Yeni türettiğimiz veriler (içerik hash'i) bu yüzden <see cref="SidecarHashStore"/> içinde yaşıyor.
/// </summary>
public sealed class LegacyDocumentRepository(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public sealed record Row(
        long Id, string FileName, string DocumentType, string Department, string UploadedBy,
        long SizeBytes, DateTime CreatedAt, string? StoragePath, string? ContentText);

    private const string SelectColumns =
        "Id, FileName, DocumentType, Department, UploadedBy, SizeBytes, CreatedAt, StoragePath, ContentText";

    public List<Row> ReadAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {SelectColumns} FROM Documents";

        var rows = new List<Row>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Map(reader));
        return rows;
    }

    public long Insert(string fileName, string documentType, string department, string uploadedBy,
        long sizeBytes, DateTime createdAt, string storagePath, string? contentText)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Documents (FileName, DocumentType, Department, UploadedBy, SizeBytes, CreatedAt, StoragePath, ContentText)
            VALUES ($fileName, $documentType, $department, $uploadedBy, $sizeBytes, $createdAt, $storagePath, $contentText);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$documentType", documentType);
        command.Parameters.AddWithValue("$department", department);
        command.Parameters.AddWithValue("$uploadedBy", uploadedBy);
        command.Parameters.AddWithValue("$sizeBytes", sizeBytes);
        command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
        command.Parameters.AddWithValue("$storagePath", storagePath);
        command.Parameters.AddWithValue("$contentText", contentText ?? string.Empty);

        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>
    /// Var olan <c>ContentText</c> kolonunun BOŞ hücrelerini doldurur.
    ///
    /// Neden bunu "şema değişikliği" saymıyorum: yeni kolon/tablo/indeks yaratmıyor, mevcut
    /// bir kolona veri yazıyor - yani upload'ın zaten yaptığı şeyi geçmişe dönük yapıyor.
    /// Dolu hücrelere dokunulmaması bilinçli: mevcut sistemin ürettiği veriyi ezme yetkimiz yok.
    /// </summary>
    public int FillEmptyContentText(long id, string contentText)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Documents SET ContentText = $text
            WHERE Id = $id AND (ContentText IS NULL OR TRIM(ContentText) = '')
            """;
        command.Parameters.AddWithValue("$text", contentText);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery();
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        return connection;
    }

    private static Row Map(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        DateTime.Parse(reader.GetString(6), null,
            System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));

    /// <summary>
    /// Legacy satırını indeks kaydına çevirir.
    /// Metnin TAMAMI aranabilir (IndexableContent, indeksleme sonrası serbest bırakılır),
    /// ama RAM'de kalıcı olarak sadece snippet için gereken baş kısmı tutulur.
    /// </summary>
    public static DocumentEntry ToEntry(Row row, string? sha256)
    {
        const int snippetLimit = 800;
        var content = row.ContentText ?? string.Empty;
        return new DocumentEntry
        {
            IndexableContent = content,
            Id = row.Id,
            FileName = row.FileName,
            DocumentType = row.DocumentType,
            Department = row.Department,
            UploadedBy = row.UploadedBy,
            SizeBytes = row.SizeBytes,
            CreatedAt = row.CreatedAt,
            StoragePath = row.StoragePath,
            // Bir kerelik dosya sistemi kontrolü. Bedeli: açılışta doküman başına bir stat
            // çağrısı. Alternatifi (her arama sonucunda kontrol) okuma kilidi altında I/O
            // yapmak olurdu; o daha kötü.
            FileAvailable = !string.IsNullOrWhiteSpace(row.StoragePath) && File.Exists(row.StoragePath),
            SnippetSource = content.Length > snippetLimit ? content[..snippetLimit] : content,
            Sha256 = sha256,
            NameSignature = TextNormalizer.NameSignature(row.FileName),
        };
    }
}
