namespace DocArchive.Api.Data;

/// <summary>
/// SADECE DEMO. Gerçek kurulumda mevcut veritabanı zaten var; bu sınıf devre dışı bırakılır
/// (appsettings: Legacy:BootstrapDemoData = false). Buradaki CREATE TABLE "mevcut sistemi
/// taklit etmek" içindir - çözümün çalışan yolları hiçbir yerde şema değiştirmez.
/// </summary>
public static class DemoDataBootstrapper
{
    private const string LegacySchema = """
        CREATE TABLE IF NOT EXISTS Documents (
            Id            INTEGER PRIMARY KEY AUTOINCREMENT,
            FileName      TEXT NOT NULL,
            DocumentType  TEXT NOT NULL,
            Department    TEXT NOT NULL,
            UploadedBy    TEXT NOT NULL,
            SizeBytes     INTEGER NOT NULL,
            CreatedAt     TEXT NOT NULL,
            StoragePath   TEXT NOT NULL,
            ContentText   TEXT
        );
        """;

    private sealed record Seed(
        string FileName, string Type, string Department, string User, int DaysAgo,
        string ContentKey, string Content,
        bool Hashed = true,
        // Kopya grupları için ortak başlık: aynı başlık + aynı gövde = birebir aynı byte'lar.
        string? Title = null,
        // true ise ContentText boş bırakılır - "içeriği hiç çıkarılmamış eski arşiv" senaryosu.
        bool ContentNotExtracted = false,
        // true ise metin katmanı olmayan PDF üretilir - taranmış belge senaryosu.
        bool Scanned = false);

    public static void Ensure(
        LegacyDocumentRepository repository, SidecarHashStore sidecar, string blobDirectory, ILogger logger)
    {
        using var connection = repository.Open();
        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = LegacySchema;
            ddl.ExecuteNonQuery();
        }

        using (var count = connection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM Documents";
            if (Convert.ToInt64(count.ExecuteScalar()) > 0)
            {
                // Demo verisi var ama dosyaları/hash'leri eksik olabilir: önceki bir sürümle
                // oluşturulmuş, ya da storage klasörü elle temizlenmiş olabilir. Bu durumda
                // "kayıt var" diye geçip gitmek, dokümana tıklanınca sessizce çalışmayan bir
                // arayüz bırakıyor. O yüzden eksikleri onarıyoruz.
                Repair(connection, sidecar, blobDirectory, logger);
                return;
            }
        }

        Directory.CreateDirectory(blobDirectory);
        var seeds = BuildSeeds();
        using var transaction = connection.BeginTransaction();
        var inserted = new List<(long Id, Seed Seed, string Sha256)>();

        foreach (var seed in seeds)
        {
            // Uzantısıyla gerçekten uyumlu bir dosya üret ve içerik adresli olarak yaz.
            // Aynı ContentKey'i paylaşan seed'ler aynı başlık + aynı gövdeyi kullandığı için
            // byte'ları da birebir aynı olur; yani kopya grupları gerçek hash eşitliğinden doğar.
            var title = seed.Title ?? Path.GetFileNameWithoutExtension(seed.FileName).Replace('_', ' ');
            var bytes = seed.Scanned
                ? DemoFileFactory.CreateScannedPdf(title)
                : DemoFileFactory.Create(seed.FileName, title, seed.Content);

            var sha256 = SidecarHashStore.ComputeSha256(bytes);
            var extension = Path.GetExtension(seed.FileName).ToLowerInvariant();
            var blobPath = Path.Combine(blobDirectory, sha256 + extension);
            if (!File.Exists(blobPath)) File.WriteAllBytes(blobPath, bytes);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO Documents (FileName, DocumentType, Department, UploadedBy, SizeBytes, CreatedAt, StoragePath, ContentText)
                VALUES ($f, $t, $d, $u, $s, $c, $p, $x);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$f", seed.FileName);
            command.Parameters.AddWithValue("$t", seed.Type);
            command.Parameters.AddWithValue("$d", seed.Department);
            command.Parameters.AddWithValue("$u", seed.User);
            command.Parameters.AddWithValue("$s", bytes.LongLength);
            command.Parameters.AddWithValue("$c", DateTime.UtcNow.AddDays(-seed.DaysAgo).ToString("O"));
            command.Parameters.AddWithValue("$p", blobPath);
            // Bilinçli boşluk: bazı dokümanların ContentText'i boş - mevcut sistemin
            // içerik çıkarımı hiç yapmadığı gerçekçi durum. /api/admin/reindex-content bunları doldurur.
            command.Parameters.AddWithValue("$x", seed.ContentNotExtracted || seed.Scanned ? string.Empty : seed.Content);
            inserted.Add((Convert.ToInt64(command.ExecuteScalar()), seed, sha256));
        }
        transaction.Commit();

        // "Tek seferlik hash backfill işi" simülasyonu.
        // Bilinçli boşluk: taranmış dosyaların hash'i yok (Hashed = false). Bu dokümanlar
        // için duplicate tespiti isim benzerliği sinyaline düşüyor.
        foreach (var (id, _, sha256) in inserted.Where(i => i.Seed.Hashed))
            sidecar.Set(id, sha256);

        logger.LogInformation(
            "Demo verisi hazır: {Docs} doküman, {Hashes} hash, {Empty} tanesinin içeriği çıkarılmamış durumda.",
            inserted.Count, sidecar.Count,
            inserted.Count(i => i.Seed.ContentNotExtracted || i.Seed.Scanned));
    }

    /// <summary>
    /// SADECE DEMO onarımı. Var olan demo satırları için dosyası kayıp olanların blob'unu
    /// yeniden üretir, StoragePath/SizeBytes değerlerini düzeltir ve sidecar hash'ini geri yazar.
    /// Kullanıcının kendi yüklediği dokümanlar yeniden üretilemez; onlar "dosyası yok" olarak kalır.
    /// </summary>
    private static void Repair(
        Microsoft.Data.Sqlite.SqliteConnection connection, SidecarHashStore sidecar,
        string blobDirectory, ILogger logger)
    {
        // Doldurma verisi aynı dosya adını birden fazla üretebiliyor (gerçek arşivlerde de olur).
        // Onarımda ilk eşleşmeyi kullanıyoruz: demo verisi için yeterli, üretim yolunu etkilemiyor.
        var seeds = BuildSeeds()
            .GroupBy(s => s.FileName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var rows = new List<(long Id, string FileName, string? StoragePath)>();

        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT Id, FileName, StoragePath FROM Documents";
            using var reader = select.ExecuteReader();
            while (reader.Read())
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        Directory.CreateDirectory(blobDirectory);
        int repaired = 0, hashRestored = 0, unrecoverable = 0;

        foreach (var (id, fileName, storagePath) in rows)
        {
            var fileExists = !string.IsNullOrWhiteSpace(storagePath) && File.Exists(storagePath);

            if (!seeds.TryGetValue(fileName, out var seed))
            {
                if (!fileExists) unrecoverable++; // kullanıcının yüklediği doküman: yeniden üretilemez
                continue;
            }

            var title = seed.Title ?? Path.GetFileNameWithoutExtension(seed.FileName).Replace('_', ' ');
            var bytes = seed.Scanned
                ? DemoFileFactory.CreateScannedPdf(title)
                : DemoFileFactory.Create(seed.FileName, title, seed.Content);
            var sha256 = SidecarHashStore.ComputeSha256(bytes);
            var blobPath = Path.Combine(blobDirectory, sha256 + Path.GetExtension(seed.FileName).ToLowerInvariant());

            if (!File.Exists(blobPath)) File.WriteAllBytes(blobPath, bytes);

            if (!fileExists || storagePath != blobPath)
            {
                using var update = connection.CreateCommand();
                update.CommandText = "UPDATE Documents SET StoragePath = $p, SizeBytes = $s WHERE Id = $id";
                update.Parameters.AddWithValue("$p", blobPath);
                update.Parameters.AddWithValue("$s", bytes.LongLength);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();
                repaired++;
            }

            if (seed.Hashed && sidecar.Get(id) is null)
            {
                sidecar.Set(id, sha256);
                hashRestored++;
            }
        }

        if (repaired + hashRestored + unrecoverable == 0)
        {
            logger.LogInformation("Demo verisi zaten mevcut ve eksiksiz.");
            return;
        }

        logger.LogWarning(
            "Demo verisi onarıldı: {Repaired} dosya yeniden üretildi, {Hashes} hash geri yazıldı, {Lost} doküman kurtarılamadı (kullanıcı yüklemesi).",
            repaired, hashRestored, unrecoverable);
    }

    // Kopya gruplarının paylaştığı başlık + gövde. Aynı olmaları, aynı byte'ları
    // (dolayısıyla aynı hash'i) üretmenin tek koşulu.
    private const string AcmeTitle = "ACME Bilişim Hizmet Sözleşmesi 2025";
    private const string AcmeBody =
        "ACME Bilişim A.Ş. ile imzalanan yıllık hizmet sözleşmesi. Sözleşme bedeli 480.000 TL, süre 12 ay, " +
        "fesih bildirimi 30 gün. Yetkili: Ayşe Demir. Ödemeler aylık eşit taksitlerle yapılır.";

    private const string BetaTitle = "Beta Yazılım Q1 Lisans Teklifi";
    private const string BetaBody =
        "Beta Yazılım Ltd. için hazırlanan Q1 lisans teklifi. 150 kullanıcı, birim fiyat 1.250 TL, " +
        "geçerlilik 15 gün. Kurulum ve eğitim bedeli teklife dahildir.";

    private const string GammaTitle = "Gamma Lojistik Mart 2025 Nakliye Faturası";
    private const string GammaBody =
        "Gamma Lojistik A.Ş. mart ayı nakliye faturası. Tutar 87.400 TL, KDV dahil, vade 30 gün, " +
        "fatura no GL-2025-0311. Taşıma güzergahı İstanbul - Ankara.";

    private static List<Seed> BuildSeeds()
    {
        // Elle yazılmış senaryolar: geri bildirimlerdeki gerçek acı noktalarını temsil ediyor.
        var seeds = new List<Seed>
        {
            // --- Kopya grubu 1: aynı sözleşme üç farklı isimle. Ortak Title sayesinde
            // üretilen dosyaların byte'ları birebir aynı olur, hash de doğal olarak eşleşir.
            new("ACME_Bilisim_Hizmet_Sozlesmesi_2025.pdf", "Sözleşme", "Hukuk", "ayse.demir", 96, "acme-sozlesme-2025",
                AcmeBody, Title: AcmeTitle),
            new("ACME sözleşme son hali.pdf", "Sözleşme", "Satış", "mehmet.kaya", 88, "acme-sozlesme-2025",
                AcmeBody, Title: AcmeTitle),
            new("acme_sozlesme_FINAL_v2 (1).pdf", "Sözleşme", "Hukuk", "ayse.demir", 61, "acme-sozlesme-2025",
                AcmeBody, Title: AcmeTitle),

            // --- Kopya grubu 2: DOCX
            new("Teklif_Beta_Yazilim_Q1.docx", "Teklif", "Satış", "mehmet.kaya", 54, "beta-teklif-q1",
                BetaBody, Title: BetaTitle),
            new("Beta Yazılım teklif kopya.docx", "Teklif", "Satış", "zeynep.ari", 40, "beta-teklif-q1",
                BetaBody, Title: BetaTitle),

            // --- Kopya grubu 3: fatura, biri "_imzali" ekiyle
            new("Fatura_2025_03_Gamma_Lojistik.pdf", "Fatura", "Finans", "burak.sahin", 132, "gamma-fatura-2025-03",
                GammaBody, Title: GammaTitle),
            new("Fatura_2025_03_Gamma_Lojistik_imzali.pdf", "Fatura", "Finans", "burak.sahin", 120, "gamma-fatura-2025-03",
                GammaBody, Title: GammaTitle),

            // --- Tekil dokümanlar. ContentNotExtracted = true olanlar, mevcut sistemin
            // içerik çıkarımı hiç yapmadığı dokümanlar; reindex sonrası aranabilir hale gelirler.
            new("Delta_Enerji_Cerceve_Sozlesmesi.pdf", "Sözleşme", "Hukuk", "ayse.demir", 210, "delta-cerceve",
                "Delta Enerji ile çerçeve tedarik sözleşmesi. Yıllık taahhüt 2.000.000 TL, gizlilik hükümleri ve tahkim şartı içerir. Uyuşmazlıklarda İstanbul Tahkim Merkezi yetkilidir.",
                ContentNotExtracted: true),
            new("IT_Donanim_Alim_Teklifi.xlsx", "Teklif", "Bilgi Teknolojileri", "can.ozturk", 33, "it-donanim-teklif",
                "Sunucu ve dizüstü bilgisayar alım teklifi. 40 adet dizüstü, 4 adet sunucu, toplam 1.180.000 TL. Tedarikçi: Nova Teknoloji."),
            new("Ofis_Kira_Sozlesmesi_2024.pdf", "Sözleşme", "İdari İşler", "elif.yildiz", 430, "ofis-kira-2024",
                "Maslak ofis kira sözleşmesi. Aylık kira 210.000 TL, artış oranı TÜFE, depozito 3 aylık kira. Kiraya veren: Vega Gayrimenkul.",
                ContentNotExtracted: true),
            new("Personel_Egitim_Hizmeti_Teklifi.docx", "Teklif", "İnsan Kaynakları", "zeynep.ari", 21, "ik-egitim-teklif",
                "Liderlik gelişim programı eğitim teklifi. 3 modül, 25 katılımcı, kişi başı 4.800 TL. Eğitim sağlayıcı: Sigma Akademi."),
            new("Fatura_2024_11_Omega_Danismanlik.pdf", "Fatura", "Finans", "burak.sahin", 265, "omega-fatura-2024-11",
                "Omega Danışmanlık kasım ayı danışmanlık faturası. Tutar 62.000 TL, fatura no OD-2024-1142, vade 15 gün.",
                ContentNotExtracted: true),
            new("Gizlilik_Sozlesmesi_NDA_Sablon.docx", "Sözleşme", "Hukuk", "ayse.demir", 380, "nda-sablon",
                "Karşılıklı gizlilik sözleşmesi şablonu. Gizlilik süresi 5 yıl, cezai şart 250.000 TL. Kullanım: tüm departmanlar."),
            new("Epsilon_Bakim_Sozlesmesi_2025.pdf", "Sözleşme", "Bilgi Teknolojileri", "can.ozturk", 47, "epsilon-bakim",
                "Epsilon Sistem bakım ve destek sözleşmesi. SLA 4 saat müdahale, yıllık bedel 360.000 TL, 7/24 destek hattı.",
                ContentNotExtracted: true),

            // --- Farklı biçimler: içerik çıkarımının OOXML dışındaki yollarını da gösteriyor.
            new("Toplanti_Notlari_Tedarik_Komitesi.html", "Teklif", "Satış", "mehmet.kaya", 12, "tedarik-komitesi-notu",
                "Tedarik komitesi toplantı notları. Kappa Tekstil teklifi revize edilecek, Lambda Medikal ile fiyat görüşmesi 2 hafta içinde yapılacak. Karar: mevcut çerçeve sözleşme uzatılmayacak."),
            new("Ihale_Sartnamesi_Ozet.rtf", "Sözleşme", "Hukuk", "ayse.demir", 27, "ihale-sartnamesi",
                "İhale şartnamesi özeti. Geçici teminat oranı yüzde 3, kesin teminat yüzde 6. Teklif geçerlilik süresi 60 gün. İtiraz süresi 10 gün."),
            new("Cerceve_Sozlesme_Sablonu.odt", "Sözleşme", "Hukuk", "ayse.demir", 300, "cerceve-sablon-odt",
                "Çerçeve sözleşme şablonu. Taraflar, süre, ödeme koşulları ve mücbir sebep maddelerini içerir. Damga vergisi taraflarca yarı yarıya karşılanır."),
            new("Eski_Arsiv_Sozlesmesi_1998.doc", "Sözleşme", "İdari İşler", "elif.yildiz", 620, "eski-arsiv-doc",
                "1998 tarihli eski arşiv sözleşmesi. Bu dosya eski ikili Word biçiminde saklanıyor.",
                ContentNotExtracted: true),

            // --- Taranmış PDF'ler: metin katmanı YOK, hash de yok.
            // Duplicate tespitinin isim benzerliğine, içerik aramasının da OCR'a düştüğü durum.
            new("Taranmis_Sozlesme_2023_ARSIV.pdf", "Sözleşme", "İdari İşler", "elif.yildiz", 520, "taranmis-1",
                "", Hashed: false, Title: "Taranmis Sozlesme 2023 ARSIV", Scanned: true),
            new("Taranmis Sozlesme 2023 arsiv kopya.pdf", "Sözleşme", "İdari İşler", "elif.yildiz", 480, "taranmis-2",
                "", Hashed: false, Title: "Taranmis Sozlesme 2023 arsiv kopya", Scanned: true),
        };

        // Doldurma verisi: liste/filtre/sayfalama davranışının gerçekçi görünmesi için.
        var vendors = new[] { "Nova Teknoloji", "Vega Gayrimenkul", "Sigma Akademi", "Kappa Tekstil", "Omega Danışmanlık", "Zeta Sigorta", "Theta İnşaat", "Lambda Medikal" };
        var types = new[] { "Sözleşme", "Teklif", "Fatura" };
        var departments = new[] { "Hukuk", "Finans", "Satış", "Bilgi Teknolojileri", "İnsan Kaynakları", "İdari İşler" };
        var users = new[] { "ayse.demir", "mehmet.kaya", "burak.sahin", "can.ozturk", "zeynep.ari", "elif.yildiz" };
        var random = new Random(1337); // deterministik demo

        for (var i = 0; i < 40; i++)
        {
            var type = types[i % types.Length];
            var vendor = vendors[i % vendors.Length];
            var department = departments[random.Next(departments.Length)];
            var user = users[random.Next(users.Length)];
            var daysAgo = 5 + random.Next(500);
            var date = DateTime.UtcNow.AddDays(-daysAgo);
            var slug = vendor.Replace(' ', '_').Replace("ı", "i").Replace("İ", "I").Replace("ş", "s");
            var fileName = type switch
            {
                "Fatura" => $"Fatura_{date:yyyy_MM}_{slug}.pdf",
                "Teklif" => $"Teklif_{slug}_{date:yyyy}_{i:D2}.docx",
                _ => $"{slug}_Sozlesme_{date:yyyy}.pdf",
            };
            var content = type switch
            {
                "Fatura" => $"{vendor} tarafından düzenlenen fatura. Tutar {random.Next(10, 900) * 1000} TL, vade {random.Next(15, 60)} gün, fatura no {slug[..3].ToUpperInvariant()}-{date:yyyyMM}-{i:D4}.",
                "Teklif" => $"{vendor} için hazırlanan ticari teklif. Kalem sayısı {random.Next(3, 20)}, toplam {random.Next(50, 1500) * 1000} TL, geçerlilik {random.Next(7, 45)} gün.",
                _ => $"{vendor} ile yapılan sözleşme. Süre {random.Next(1, 5)} yıl, yıllık bedel {random.Next(100, 2000) * 1000} TL, fesih bildirimi {random.Next(15, 90)} gün.",
            };

            seeds.Add(new Seed(fileName, type, department, user, daysAgo, $"filler-{i}", content));
        }

        return seeds;
    }
}
