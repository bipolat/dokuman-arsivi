# Kurulum ve Ayağa Kaldırma

Bu dosya **bulunduğu klasörü proje kökü kabul eder.** Bilgisayarda nereye koyarsanız
(`C:\projeler\arsiv`, `D:\work\dms`, masaüstü — fark etmez) adımlar o klasöre göre çalışır.
Sabit yol yazılmadı.

## Ne yapar

```
1) backend/ ve frontend/ klasörleri var mı?  →  yoksa oluştur
2) Repoları çek                              →  ŞİMDİLİK ATLANDI (aşağıda yeri hazır)
3) Kapı kontrolü: içlerinde gerçek proje var mı?
4) Varsa → backend'i ayağa kaldır  (http://localhost:5099)
5) Varsa → frontend'i ayağa kaldır (http://localhost:5173)
```

3. adım bilinçli bir kapı: 2. adım atlandığı için klasörler **boş** kalmış olabilir. Boş klasörü
ayağa kaldırmaya çalışmak anlamsız hata verir, o yüzden önce içerik kontrol edilip anlaşılır bir
mesajla duruluyor.

## Gereksinimler

| | Sürüm | Kontrol |
|---|---|---|
| .NET SDK | 8.0+ (bu projede 10.0 RC ile geliştirildi) | `dotnet --version` |
| Node.js | 20+ (bu projede 25 ile geliştirildi) | `node --version` |
| Git | 2.x (2. adım açıldığında gerekir) | `git --version` |

---

## Tek seferde çalıştır

**Bu dosyanın bulunduğu klasörde** PowerShell açın (klasöre sağ tık → *Terminalde aç*), aşağıdaki
bloğu olduğu gibi yapıştırın.

> Dilerseniz aynı bloğu bu klasöre `kurulum.ps1` olarak kaydedip
> `powershell -ExecutionPolicy Bypass -File .\kurulum.ps1` ile çalıştırabilirsiniz —
> script olarak da, yapıştırma olarak da aynı şekilde çalışır.

```powershell
# --- 0) Proje kökünü bul (script olarak da, yapistirma olarak da calisir) ---
if ($PSScriptRoot) { $root = $PSScriptRoot } else { $root = (Get-Location).Path }
$backend  = Join-Path $root 'backend'
$frontend = Join-Path $root 'frontend'
Write-Host "Proje koku : $root" -ForegroundColor Cyan

# --- 1) Klasorler yoksa olustur ---
foreach ($dir in @($backend, $frontend)) {
    if (Test-Path $dir) {
        Write-Host "  [var]      $(Split-Path $dir -Leaf)"
    } else {
        New-Item -ItemType Directory -Path $dir | Out-Null
        Write-Host "  [olustu]   $(Split-Path $dir -Leaf)" -ForegroundColor Yellow
    }
}

# --- 2) Repolari cek --- SIMDILIK ATLANDI ---
# Kullanmaya baslarken asagidaki iki satirin basindaki # isaretini kaldirin ve URL'leri yazin.
# Klasor bos degilse git clone hata verir; o yuzden bos kontrolu eklendi.
#
# if (-not (Get-ChildItem $backend  -Force | Select-Object -First 1)) { git clone <BACKEND_REPO_URL>  $backend  }
# if (-not (Get-ChildItem $frontend -Force | Select-Object -First 1)) { git clone <FRONTEND_REPO_URL> $frontend }
Write-Host "  [atlandi]  repo cekme adimi" -ForegroundColor DarkGray

# --- 3) Kapi kontrolu: iceride gercek proje var mi? ---
$csproj  = Get-ChildItem -Path $backend -Filter *.csproj -Recurse -ErrorAction SilentlyContinue |
           Select-Object -First 1
$pkgJson = Join-Path $frontend 'package.json'

$hazir = $true
if ($null -eq $csproj) {
    Write-Warning "backend/ icinde .csproj bulunamadi."
    $hazir = $false
}
if (-not (Test-Path $pkgJson)) {
    Write-Warning "frontend/ icinde package.json bulunamadi."
    $hazir = $false
}

if (-not $hazir) {
    Write-Host ""
    Write-Host "Klasorler hazir ama icleri bos. Ayaga kaldirilacak proje yok." -ForegroundColor Red
    Write-Host "Yapilacak: 2. adimdaki git clone satirlarini acip repo URL'lerini yazin," -ForegroundColor Red
    Write-Host "ya da proje dosyalarini bu klasorlere elle kopyalayin, sonra tekrar calistirin." -ForegroundColor Red
} else {
    Write-Host ""
    Write-Host "Proje bulundu:" -ForegroundColor Green
    Write-Host "  backend  : $($csproj.FullName)"
    Write-Host "  frontend : $pkgJson"

    # --- 4) Backend ---
    Write-Host ""
    Write-Host "Backend ayaga kaldiriliyor (http://localhost:5099) ..." -ForegroundColor Cyan
    $backendCmd = "Set-Location '$root'; dotnet run --project '$($csproj.FullName)' --no-launch-profile --urls http://localhost:5099"
    Start-Process powershell -ArgumentList '-NoExit', '-Command', $backendCmd

    # Health endpoint cevap verene kadar bekle (ilk calistirmada demo verisi uretilir, biraz surer)
    $up = $false
    for ($i = 1; $i -le 90; $i++) {
        Start-Sleep -Seconds 1
        try {
            $h = Invoke-RestMethod 'http://localhost:5099/api/health' -TimeoutSec 2
            if ($h.status -eq 'ok') {
                Write-Host "  backend hazir - $($h.indexedDocuments) dokuman indekslendi ($i sn)" -ForegroundColor Green
                $up = $true
                break
            }
        } catch { }
    }
    if (-not $up) {
        Write-Warning "Backend 90 saniyede cevap vermedi. Acilan pencerede hata mesaji olabilir."
    }

    # --- 5) Frontend ---
    Write-Host ""
    if (-not (Test-Path (Join-Path $frontend 'node_modules'))) {
        Write-Host "npm install calisiyor (ilk kurulumda birkac dakika surebilir) ..." -ForegroundColor Cyan
        Push-Location $frontend
        npm install
        Pop-Location
    } else {
        Write-Host "node_modules mevcut, npm install atlandi."
    }

    Write-Host "Frontend ayaga kaldiriliyor (http://localhost:5173) ..." -ForegroundColor Cyan
    Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$frontend'; npm run dev"
    Start-Sleep -Seconds 6

    # Vite 5173 mesgulse 5174'e kayar; ikisini de dene
    $uiPort = $null
    foreach ($port in 5173, 5174, 5175) {
        try {
            $r = Invoke-WebRequest "http://localhost:$port/" -UseBasicParsing -TimeoutSec 2
            if ($r.StatusCode -eq 200) { $uiPort = $port; break }
        } catch { }
    }

    Write-Host ""
    Write-Host "=================================================" -ForegroundColor Green
    if ($uiPort) {
        Write-Host " Arayuz  : http://localhost:$uiPort" -ForegroundColor Green
    } else {
        Write-Host " Arayuz  : frontend penceresindeki adrese bakin" -ForegroundColor Yellow
    }
    Write-Host " API     : http://localhost:5099/api/health" -ForegroundColor Green
    Write-Host "=================================================" -ForegroundColor Green
    Write-Host "Durdurmak icin acilan iki PowerShell penceresinde Ctrl+C."
}
```

---

## Adım adım ne oluyor

### 1. Klasör kontrolü

`backend/` ve `frontend/` yoksa oluşturulur, varsa dokunulmaz. Mevcut bir kuruluma zarar vermez.

### 2. Repo çekme — şimdilik atlandı

Blokta yeri hazır, `#` ile kapatılmış. Açmak için iki satırın başındaki `#` kaldırılıp repo
URL'leri yazılır. Klasör boş değilse `git clone` hata verdiği için boşluk kontrolü de eklendi —
yani ikinci çalıştırmada üzerine yazmaya çalışmaz.

### 3. Kapı kontrolü

2. adım atlandığı için klasörler boş kalabilir. Bu yüzden ayağa kaldırmadan önce:

- `backend/` içinde **herhangi bir** `.csproj` aranır (alt klasörler dahil)
- `frontend/package.json` aranır

İkisinden biri yoksa süreç **anlaşılır bir mesajla durur**. `.csproj` alt klasörlerde de aranıyor,
çünkü repo `backend/DocArchive.Api/DocArchive.Api.csproj` gibi bir yapıda gelebilir.

### 4. Backend

Ayrı bir PowerShell penceresinde `dotnet run` ile 5099 portunda başlar. Sonra
`/api/health` endpoint'i cevap verene kadar (en fazla 90 sn) beklenir.

> **İlk çalıştırmada** demo verisi otomatik oluşur: 60 doküman + gerçek PDF/DOCX/XLSX/ODT/RTF/HTML
> dosyaları. Bu yüzden ilk açılış sonrakilerden birkaç saniye uzun sürer.

### 5. Frontend

`node_modules` yoksa `npm install` çalışır (varsa atlanır), sonra ayrı bir pencerede `npm run dev`
başlar. Port 5173 doluysa Vite kendiliğinden 5174'e kayar; script üç portu da yoklayıp doğru
adresi yazdırır.

**5099 portu önemli:** frontend'in `vite.config.ts` dosyası `/api` isteklerini
`http://localhost:5099` adresine proxy'liyor. Backend başka bir portta çalışırsa arayüz boş görünür.

---

## Doğrulama

Her şey yolundaysa:

```powershell
# API cevap veriyor mu
Invoke-RestMethod 'http://localhost:5099/api/health'
# beklenen: status=ok, indexedDocuments=60

# Arama calisiyor mu (Turkce katlama testi)
(Invoke-RestMethod 'http://localhost:5099/api/documents?q=sozlesme').total
# beklenen: 20+ sonuc

# Tam senaryo testi (23 senaryo) - API ayaktayken
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

Arayüzde görmesi gerekenler: üstte gösterge şeridi, arama kutusu, filtreler, doküman listesi,
sağda yükleme paneli, gösterge şeridinin en sağında **i** butonu (proje notları).

---

## Sorun giderme

| Belirti | Sebep / çözüm |
|---|---|
| `dotnet: command not found` | .NET SDK kurulu değil → https://dotnet.microsoft.com/download |
| Backend açılıyor, sonra kapanıyor | 5099 portu meşgul. `Get-NetTCPConnection -LocalPort 5099` ile bakın, ya da script'teki `--urls` değerini ve `vite.config.ts`'teki proxy hedefini birlikte değiştirin |
| Arayüz açılıyor ama liste boş | Backend 5099'da değil. `Invoke-RestMethod http://localhost:5099/api/health` ile doğrulayın |
| `npm install` hata veriyor | `frontend/node_modules` ve `package-lock.json` silinip tekrar denenir |
| Demo verisini sıfırlamak | `backend/DocArchive.Api/storage` klasörünü silin, backend'i yeniden başlatın. Klasör kilitli derse editörü (VS Code vb.) kapatıp tekrar deneyin |
| Türkçe karakterler bozuk görünüyor | Windows PowerShell 5.1 için: `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` |
| Script'i çalıştıramıyorum | `powershell -ExecutionPolicy Bypass -File .\kurulum.ps1` |

## Notlar

- Script **idempotent**: birden fazla çalıştırılabilir. Var olan klasörlere, mevcut
  `node_modules`'a ve oluşmuş demo verisine dokunmaz.
- Backend ve frontend **ayrı pencerelerde** açılır (`-NoExit`), böylece logları görebilir ve
  `Ctrl+C` ile durdurabilirsiniz.
- Tek süreçte çalıştırmak isterseniz: `cd frontend; npm run build` → build çıktısı API'nin
  `wwwroot` klasörüne gider, sonra yalnızca **http://localhost:5099** yeterli olur.
- 2. adım (repo çekme) doldurulduğunda bu dosyada başka bir değişiklik gerekmez.
