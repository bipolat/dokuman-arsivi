# Uçtan uca senaryo testi. API'nin http://localhost:5099 adresinde çalışıyor olması gerekir.
#   dotnet run --project backend/DocArchive.Api --urls http://localhost:5099
#   powershell -File scripts/smoke-test.ps1
#
# Not: bu dosya UTF-8 BOM ile kaydedilmelidir; Windows PowerShell 5.1 BOM'suz dosyaları
# ANSI olarak okur ve içindeki Türkçe metinler bozulur.

$ErrorActionPreference = 'Stop'
$base = 'http://localhost:5099/api'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Add-Type -AssemblyName System.Net.Http
$client = New-Object System.Net.Http.HttpClient

function Show($title) { Write-Host "`n=== $title ===" }

function Sha256Hex([byte[]]$bytes) {
  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    return ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLower()
  } finally { $sha.Dispose() }
}

# Dokümanın gerçek dosyasını indirir. Hash'i sabit yazmak yerine buradan hesaplıyoruz;
# böylece test, sistemin gerçekten sakladığı byte'lar üzerinden çalışıyor.
function DownloadDoc([long]$id, [bool]$asAttachment = $false) {
  $suffix = if ($asAttachment) { '?download=1' } else { '' }
  $response = $client.GetAsync("$base/documents/$id/file$suffix").Result
  return [pscustomobject]@{
    Status      = [int]$response.StatusCode
    Bytes       = $response.Content.ReadAsByteArrayAsync().Result
    ContentType = "$($response.Content.Headers.ContentType)"
    Disposition = "$($response.Content.Headers.ContentDisposition)"
  }
}

function UploadDoc([string]$path, [string]$type, [string]$dept, [string]$user, [string]$force) {
  $content = New-Object System.Net.Http.MultipartFormDataContent
  $bytes = [System.IO.File]::ReadAllBytes($path)
  $fileContent = New-Object System.Net.Http.ByteArrayContent($bytes, 0, $bytes.Length)
  $content.Add($fileContent, 'file', [System.IO.Path]::GetFileName($path))
  foreach ($pair in @(@('documentType', $type), @('department', $dept), @('uploadedBy', $user), @('force', $force))) {
    $field = New-Object System.Net.Http.StringContent($pair[1], [System.Text.Encoding]::UTF8)
    $content.Add($field, $pair[0])
  }
  $response = $client.PostAsync("$base/documents", $content).Result
  $body = $response.Content.ReadAsStringAsync().Result
  return [pscustomobject]@{ Status = [int]$response.StatusCode; Json = ($body | ConvertFrom-Json) }
}

Show '1. health'
Invoke-RestMethod "$base/health" | ConvertTo-Json -Compress

Show '2. Listeleme (arama yok - en yeniler)'
$r = Invoke-RestMethod "$base/documents?pageSize=3"
"total=$($r.total) tookMs=$($r.tookMs)"
$r.items | ForEach-Object { " - $($_.fileName) [$($_.documentType)/$($_.department)]" }

Show "3. Türkçe katlama: 'sozlesme' aramasi 'Sözleşme' dokümanlarini buluyor mu"
$r = Invoke-RestMethod "$base/documents?q=sozlesme&pageSize=3"
"total=$($r.total) tookMs=$($r.tookMs) kopyaGruplandi=$($r.collapsedDuplicates)"
$r.items | ForEach-Object { " - $($_.fileName) score=$($_.score) dupes=$($_.duplicateCount)" }

Show "4. Kopya gruplama: 'acme'"
$r = Invoke-RestMethod "$base/documents?q=acme"
"total=$($r.total) kopyaGruplandi=$($r.collapsedDuplicates) | $($r.messages -join ' ')"
$r.items | ForEach-Object {
  " - $($_.fileName) dupes=$($_.duplicateCount)"
  $_.duplicates | ForEach-Object { "     kopya: $($_.fileName) ($($_.uploadedBy))" }
}

Show '5. AND davranisi (alakasiz sonuc uretmiyor)'
"'gamma fatura' -> total=$((Invoke-RestMethod "$base/documents?q=gamma%20fatura").total)"
$r = Invoke-RestMethod "$base/documents?q=gamma%20sigorta"
"'gamma sigorta' -> total=$($r.total) | $($r.messages[0])"

Show "6. Yazim hatasi: 'sozlesne'"
$r = Invoke-RestMethod "$base/documents?q=sozlesne"
"total=$($r.total) didYouMean=$($r.didYouMean)"

Show '7. Filtre bos sonuc verdiginde eyleme donusebilir oneri'
$r = Invoke-RestMethod "$base/documents?q=acme&department=Finans"
"total=$($r.total) filtresizEslesme=$($r.matchesIgnoringFilters)"
$r.messages | ForEach-Object { " mesaj: $_" }
$r.suggestions | ForEach-Object { " oneri: [$($_.kind)] $($_.label)" }

Show '8. Precheck - sistemde birebir ayni icerik var'
$acme = DownloadDoc 1
$body = @{ fileName = 'ACME sozlesme.pdf'; sizeBytes = $acme.Bytes.Length; sha256 = (Sha256Hex $acme.Bytes) } | ConvertTo-Json
$r = Invoke-RestMethod "$base/documents/precheck" -Method Post -ContentType 'application/json; charset=utf-8' -Body $body
"verdict=$($r.verdict)"
"mesaj: $($r.message)"
$r.exactMatches | ForEach-Object { " eslesme: $($_.fileName) ($($_.department))" }

Show '9. Precheck - hash yok, isim benzerligine dusuyor'
$body = @{ fileName = 'Taranmis_Sozlesme_2023_ARSIV_kopya.pdf'; sizeBytes = 1484800; sha256 = 'deadbeef' } | ConvertTo-Json
$r = Invoke-RestMethod "$base/documents/precheck" -Method Post -ContentType 'application/json; charset=utf-8' -Body $body
"verdict=$($r.verdict) | $($r.message)"
$r.similarMatches | ForEach-Object { " benzer: $($_.fileName)" }

Show '10. Precheck - tamamen yeni dosya'
$body = @{ fileName = 'Kappa_Tedarik_Anlasmasi_Ek_Protokol.pdf'; sizeBytes = 9999; sha256 = 'cafebabe' } | ConvertTo-Json
$r = Invoke-RestMethod "$base/documents/precheck" -Method Post -ContentType 'application/json; charset=utf-8' -Body $body
"verdict=$($r.verdict) | $($r.message)"

Show '11. Upload - yeni dosya'
$tmp = Join-Path $env:TEMP 'Zeta_Sigorta_Grup_Saglik_Sozlesmesi_2026.txt'
Set-Content -Path $tmp -Value 'Zeta Sigorta ile 2026 yili grup saglik police sozlesmesi. Yillik prim 1.450.000 TL, 320 calisan kapsaminda.' -Encoding UTF8
$r = UploadDoc $tmp 'Sözleşme' 'İnsan Kaynakları' 'zeynep.ari' 'false'
"http=$($r.Status) verdict=$($r.Json.verdict) id=$($r.Json.document.id) departman=$($r.Json.document.department)"
"mesaj: $($r.Json.message)"

Show '12. Upload - ayni dosya tekrar (engellenmeli)'
$r = UploadDoc $tmp 'Sözleşme' 'İnsan Kaynakları' 'zeynep.ari' 'false'
"http=$($r.Status) verdict=$($r.Json.verdict)"
"mesaj: $($r.Json.message)"

Show '13. Upload - FARKLI isim, AYNI icerik (hash yakalamali)'
$tmp2 = Join-Path $env:TEMP 'zeta police son hali.txt'
Copy-Item $tmp $tmp2 -Force
$r = UploadDoc $tmp2 'Sözleşme' 'Hukuk' 'ayse.demir' 'false'
"http=$($r.Status) verdict=$($r.Json.verdict)"
"mesaj: $($r.Json.message)"

Show '14. Upload - force ile bilincli onay'
$r = UploadDoc $tmp2 'Sözleşme' 'Hukuk' 'ayse.demir' 'true'
"http=$($r.Status) verdict=$($r.Json.verdict) id=$($r.Json.document.id)"
"mesaj: $($r.Json.message)"

Show '15. Yeni dokuman aramada gorunuyor mu (icerik metni dahil)'
$r = Invoke-RestMethod "$base/documents?q=police%20prim"
"total=$($r.total) kopyaGruplandi=$($r.collapsedDuplicates)"
$r.items | ForEach-Object { " - $($_.fileName) dupes=$($_.duplicateCount)"; "   snippet: $($_.snippet)" }

Show '16. Duplicates endpoint (dokuman 1)'
$dupes = Invoke-RestMethod "$base/documents/1/duplicates"
@($dupes) | ForEach-Object { " - $($_.fileName) ($($_.department) / $($_.uploadedBy))" }

Show '17. Insights'
$r = Invoke-RestMethod "$base/insights"
"dokuman=$($r.documentCount) terim=$($r.termCount) kopyaGrubu=$($r.duplicateClusters) fazlaKopya=$($r.duplicateDocuments) bosaAlan=$([math]::Round($r.wastedBytes/1024)) KB indeksKurulum=$($r.indexBuildMs) ms"
"sonucsuz aramalar: $(($r.topZeroResultQueries | ForEach-Object { "$($_.key)($($_.count))" }) -join ', ')"
"en buyuk kopya gruplari: $(($r.topDuplicateClusters | Select-Object -First 4 | ForEach-Object { "$($_.key)=$($_.count)" }) -join ' | ')"

Show '18. Gecikme olcumu (200 arama)'
$queries = @('sozlesme', 'acme', 'fatura gamma', 'teklif', 'nova teknoloji', 'hukuk', 'burak', '2025')
$serverTimes = New-Object System.Collections.Generic.List[double]
$sw = [System.Diagnostics.Stopwatch]::StartNew()
for ($i = 0; $i -lt 200; $i++) {
  $q = [uri]::EscapeDataString($queries[$i % $queries.Length])
  $res = $client.GetStringAsync("$base/documents?q=$q").Result | ConvertFrom-Json
  $serverTimes.Add([double]$res.tookMs)
}
$sw.Stop()
$sorted = $serverTimes | Sort-Object
"200 istek toplam $($sw.ElapsedMilliseconds) ms | uctan uca ortalama $([math]::Round($sw.ElapsedMilliseconds/200,2)) ms"
"sunucu ici arama suresi: ort=$([math]::Round(($serverTimes | Measure-Object -Average).Average,3)) ms  p95=$($sorted[189]) ms  max=$($sorted[-1]) ms"

Show '19. Statik frontend servis ediliyor mu'
$page = Invoke-WebRequest 'http://localhost:5099/' -UseBasicParsing
"http=$($page.StatusCode) uzunluk=$($page.Content.Length)"

Show '20. Dokumana tiklayinca acilan dosya gercek mi (imza + icerik tipi)'
foreach ($id in 1, 4, 17, 18, 19) {
  $doc = Invoke-RestMethod "$base/documents/$id"
  $file = DownloadDoc $id
  $signature = -join ($file.Bytes[0..3] | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } })
  "{0,-42} http={1} imza='{2}' boyut={3,5} tip={4}" -f $doc.fileName, $file.Status, $signature, $file.Bytes.Length, $file.ContentType
}

Show '21. Indirme (attachment) basligi - Turkce dosya adi dahil'
$file = DownloadDoc 2 $true
"http=$($file.Status)"
"disposition: $($file.Disposition)"

Show '22. Icerik aramasi - reindex ONCESI vs SONRASI'
$sorgular = @('tahkim', 'depozito', 'teminat', 'mucbir', 'komitesi', 'dizustu')
$once = @{}
foreach ($q in $sorgular) { $once[$q] = (Invoke-RestMethod "$base/documents?q=$([uri]::EscapeDataString($q))").total }
$ins = Invoke-RestMethod "$base/insights"
"reindex oncesi: icerigi aranabilir $($ins.contentIndexedCount)/$($ins.documentCount), eksik $($ins.contentMissingCount)"

$re = Invoke-RestMethod "$base/admin/reindex-content" -Method Post
"reindex: tarandi=$($re.scanned) cikarildi=$($re.extracted) metinKatmaniYok=$($re.noTextLayer) desteklenmiyor=$($re.unsupported) sure=$($re.tookMs) ms"

foreach ($q in $sorgular) {
  $r = Invoke-RestMethod "$base/documents?q=$([uri]::EscapeDataString($q))"
  $nerede = ($r.items | Select-Object -First 1 | ForEach-Object { $_.fileName })
  "  '$q': once=$($once[$q]) -> sonra=$($r.total)   $nerede"
}
$ins = Invoke-RestMethod "$base/insights"
"reindex sonrasi: icerigi aranabilir $($ins.contentIndexedCount)/$($ins.documentCount), eksik $($ins.contentMissingCount), terim=$($ins.termCount)"

Show '23. Icerigi cikarilamayanlar dogru sebeple etiketlenmis mi'
foreach ($q in 'taranmis', '1998') {
  $r = Invoke-RestMethod "$base/documents?q=$q&collapseDuplicates=false"
  $r.items | ForEach-Object { "  $($_.fileName)`n     indeksli=$($_.contentIndexed) | $($_.contentNote)" }
}

Remove-Item $tmp, $tmp2 -Force
$client.Dispose()
Write-Host "`nTUM SENARYOLAR CALISTI"
