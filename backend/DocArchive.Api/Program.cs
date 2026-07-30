using DocArchive.Api.Data;
using DocArchive.Api.Models;
using DocArchive.Api.Search;
using DocArchive.Api.Services;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Windows-1254 gibi kod sayfaları .NET Core'da varsayılan olarak kayıtlı değil;
// eski Türkçe dosyaların içeriğini okuyabilmek ve RTF üretmek için gerekiyor.
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

var dataDir = Path.Combine(builder.Environment.ContentRootPath,
    builder.Configuration["Storage:DataDir"] ?? "storage");
Directory.CreateDirectory(dataDir);

var configured = builder.Configuration.GetConnectionString("LegacyDocuments");
var connectionString = string.IsNullOrWhiteSpace(configured)
    ? $"Data Source={Path.Combine(dataDir, "legacy-documents.db")}"
    : configured;

builder.Services.AddSingleton(new LegacyDocumentRepository(connectionString));
builder.Services.AddSingleton(new SidecarHashStore(Path.Combine(dataDir, "sidecar-hashes.jsonl")));
builder.Services.AddSingleton<DocumentIndex>();
builder.Services.AddSingleton<DocumentService>();

// Upload boyutu sınırı: sınırsız upload, RAM'de hash hesapladığımız için doğrudan bir risk.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 25 * 1024 * 1024;
});

builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy
    .WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();
app.UseCors();

// --- açılış: legacy tablodan indeksi kur ------------------------------------
{
    var repository = app.Services.GetRequiredService<LegacyDocumentRepository>();
    var sidecar = app.Services.GetRequiredService<SidecarHashStore>();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    var service = app.Services.GetRequiredService<DocumentService>();

    if (app.Configuration.GetValue("Legacy:BootstrapDemoData", true))
        DemoDataBootstrapper.Ensure(repository, sidecar, service.BlobDirectory, logger);

    service.LoadIndex();
}

// --- API --------------------------------------------------------------------

app.MapGet("/api/health", (DocumentIndex index) => Results.Ok(new
{
    status = "ok",
    indexedDocuments = index.GetInsights().DocumentCount,
    indexBuiltAt = index.BuiltAt,
}));

app.MapGet("/api/meta", (DocumentIndex index) => Results.Ok(index.GetMeta()));

app.MapGet("/api/documents", (
    DocumentIndex index,
    string? q, string? type, string? department,
    DateTime? from, DateTime? to, string? sort,
    int? page, int? pageSize, bool? collapseDuplicates) =>
    Results.Ok(index.Search(new SearchQuery
    {
        Q = q,
        Type = type,
        Department = department,
        From = from,
        To = to,
        Sort = sort,
        Page = page ?? 1,
        PageSize = pageSize ?? 20,
        CollapseDuplicates = collapseDuplicates ?? true,
    })));

app.MapGet("/api/documents/{id:long}", (long id, DocumentService service) =>
{
    var document = service.GetById(id);
    return document is null
        ? Results.NotFound(new { message = $"{id} numaralı doküman bulunamadı." })
        : Results.Ok(document);
});

app.MapGet("/api/documents/{id:long}/duplicates", (long id, DocumentService service) =>
    Results.Ok(service.GetDuplicates(id)));

// Dokümanı açma / indirme. download=1 (ya da true) ise tarayıcı indirir, aksi halde
// destekliyorsa sekmede gösterir (PDF, metin, HTML).
// download parametresi bilinçli olarak string: bool? bağlaması "1" değerini reddedip
// tüm isteği 400'e düşürüyor ve "1" bir bayrak için en doğal değer.
app.MapGet("/api/documents/{id:long}/file", (long id, string? download, DocumentService service) =>
{
    var asAttachment = download is "1" or "true" or "yes";

    var resolved = service.ResolveFile(id);
    if (resolved is null)
    {
        return Results.NotFound(new
        {
            message = "Dokümanın dosyası bu ortamda bulunamadı. Kayıt mevcut ama içeriği depoda yok.",
        });
    }

    var (path, contentType, fileName) = resolved.Value;
    return asAttachment
        ? Results.File(path, contentType, fileName, enableRangeProcessing: true)
        : Results.File(path, contentType, enableRangeProcessing: true);
});

// Yüklemeden ÖNCE uyarma adımı: tarayıcı dosyanın hash'ini hesaplayıp sorar.
// Byte'lar sunucuya gitmeden "bu zaten var" cevabı dönebiliyor.
app.MapPost("/api/documents/precheck", (PrecheckRequest request, DocumentService service) =>
{
    if (string.IsNullOrWhiteSpace(request.FileName))
        return Results.BadRequest(new { message = "Dosya adı gerekli." });
    return Results.Ok(service.Precheck(request));
});

app.MapPost("/api/documents", async (
    IFormFile file,
    [FromForm] string? documentType,
    [FromForm] string? department,
    [FromForm] string? uploadedBy,
    [FromForm] bool? force,
    DocumentService service,
    CancellationToken cancellationToken) =>
{
    if (file is null || file.Length == 0)
        return Results.BadRequest(new { message = "Dosya boş." });
    if (string.IsNullOrWhiteSpace(documentType) || string.IsNullOrWhiteSpace(department) || string.IsNullOrWhiteSpace(uploadedBy))
        return Results.BadRequest(new { message = "Tür, departman ve yükleyen alanları zorunlu." });

    await using var stream = file.OpenReadStream();
    var (statusCode, body) = await service.UploadAsync(
        stream, Path.GetFileName(file.FileName), documentType.Trim(), department.Trim(),
        uploadedBy.Trim(), force ?? false, cancellationToken);

    return Results.Json(body, statusCode: statusCode);
}).DisableAntiforgery();

// Arama kalitesini ölçmek için: sonuç dönmeyen sorgular ve kopya istatistikleri.
app.MapGet("/api/insights", (DocumentIndex index) => Results.Ok(index.GetInsights()));

// Mevcut arşivi geriye dönük aranabilir hale getirir: blob'lardan metin çıkarıp
// legacy tablodaki BOŞ ContentText hücrelerini doldurur, sonra indeksi yeniden kurar.
// Gerçek sistemde zamanlanmış bir iş olur; burada etkisi canlı görülebilsin diye endpoint.
app.MapPost("/api/admin/reindex-content", (DocumentService service) =>
    Results.Ok(service.ReindexContent()));

// Frontend build'i varsa aynı origin'den servis edilir (tek süreç, ek altyapı yok).
if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
{
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}

app.Run();
