# Dokuman Arsivi - klasor hazirligi, repo cekimi ve sunuculari ayaga kaldirma.
#
# Bu betik BULUNDUGU KLASORU proje koku kabul eder; sabit yol yoktur.
# kurulum.bat tarafindan cagrilir, ama tek basina da calisir:
#   powershell -ExecutionPolicy Bypass -File .\kurulum.ps1

param(
    # Proje bu klasorde yoksa buradan cekilir. Bos birakilirsa cekme adimi atlanir.
    [string]$RepoUrl = 'https://github.com/bipolat/dokuman-arsivi.git',
    [int]$ApiPort = 5099,
    # Sadece klasor/repo hazirligi yapip sunuculari baslatmamak icin.
    [switch]$SkipRun,
    # Betik tek basina calistirildiginda tarayiciyi da acsin.
    # kurulum.bat bunu kullanmaz; adresi dosyadan okuyup kendisi acar.
    [switch]$OpenBrowser
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

if ($PSScriptRoot) { $root = $PSScriptRoot } else { $root = (Get-Location).Path }

function Write-Step($text) { Write-Host "`n$text" -ForegroundColor Cyan }
function Write-Ok($text) { Write-Host "  [OK]    $text" -ForegroundColor Green }
function Write-Info($text) { Write-Host "  [bilgi] $text" -ForegroundColor DarkGray }
function Write-Warn2($text) { Write-Host "  [dikkat] $text" -ForegroundColor Yellow }

# Proje gercekten burada mi? Klasorun varligi yetmez, icinde proje dosyalari olmali.
function Find-Project([string]$base) {
    $backendDir = Join-Path $base 'backend'
    $frontendPkg = Join-Path $base 'frontend\package.json'
    if (-not (Test-Path $backendDir)) { return $null }
    if (-not (Test-Path $frontendPkg)) { return $null }

    $csproj = Get-ChildItem -Path $backendDir -Filter *.csproj -Recurse -ErrorAction SilentlyContinue |
              Select-Object -First 1
    if ($null -eq $csproj) { return $null }

    return [pscustomobject]@{
        Csproj      = $csproj
        FrontendDir = Join-Path $base 'frontend'
    }
}

Write-Host "Proje koku : $root" -ForegroundColor Cyan

# ------------------------------------------------------------------ 1) Klasorler
Write-Step '1) Klasor kontrolu'
$project = Find-Project $root

if ($project) {
    Write-Ok 'backend/ ve frontend/ mevcut, icinde proje var'
} else {
    foreach ($name in 'backend', 'frontend') {
        $dir = Join-Path $root $name
        if (Test-Path $dir) { Write-Info "$name/ var (ama proje dosyalari eksik)" }
        else { Write-Info "$name/ yok" }
    }
}

# --------------------------------------------------------------------- 2) Repo
Write-Step '2) Repo cekimi'

if ($project) {
    Write-Info 'Proje zaten burada, cekim gerekmiyor'
} elseif ([string]::IsNullOrWhiteSpace($RepoUrl)) {
    Write-Info 'RepoUrl bos, adim atlandi'
} else {
    Write-Info "Kaynak: $RepoUrl"
    # Gecici klasore cekip icerigi koke tasiyoruz: git clone dolu bir klasore
    # dogrudan cekemez, boylece kokte kurulum.bat gibi dosyalar olsa da calisir.
    $temp = Join-Path $env:TEMP ('clone-' + [guid]::NewGuid().ToString('N'))
    # Otomatik calisan bir betikte kimlik istemi ekrani beklemeye sokar; kapatiyoruz.
    $env:GIT_TERMINAL_PROMPT = '0'
    try {
        # git bilgi mesajlarini da stderr'e yazar. ErrorActionPreference='Stop' bunlari
        # istisnaya cevirip gercek hata satirini gizliyor; o yuzden cikis kodunu kendimiz kontrol ediyoruz.
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        $cloneOutput = git clone --depth 1 $RepoUrl $temp 2>&1
        $cloneExit = $LASTEXITCODE
        $ErrorActionPreference = $prevEap

        $cloneOutput | ForEach-Object { Write-Info $_ }
        if ($cloneExit -ne 0) {
            $reason = $cloneOutput |
                      Where-Object { $_ -match 'fatal|error|denied|not found|Authentication' } |
                      Select-Object -Last 1
            if (-not $reason) { $reason = "cikis kodu $cloneExit" }
            throw $reason
        }

        Get-ChildItem -Path $temp -Force | ForEach-Object {
            $target = Join-Path $root $_.Name
            if (Test-Path $target) {
                # Kokte ayni isimde bos bir klasor varsa (or. onceden olusturulmus backend/) kaldir
                $existing = Get-Item $target
                if ($existing.PSIsContainer -and -not (Get-ChildItem $target -Force | Select-Object -First 1)) {
                    Remove-Item $target -Force
                } else {
                    Write-Warn2 "$($_.Name) kokte zaten var, uzerine yazilmadi"
                    return
                }
            }
            Move-Item -Path $_.FullName -Destination $target -Force
        }
        Write-Ok 'Repo icerigi koke tasindi'
        $project = Find-Project $root
    } catch {
        Write-Warn2 "Cekim yapilamadi: $($_.Exception.Message)"
        Write-Warn2 'Repo henuz yayinlanmamis ya da erisim yetkisi yok olabilir.'
    } finally {
        if (Test-Path $temp) { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ------------------------------------------------------------------- 3) Kapi
Write-Step '3) Ayaga kaldirma oncesi kontrol'

if (-not $project) {
    # Istenen davranis: klasorler yoksa olusturulsun. Ama icleri bos oldugu icin
    # sunuculari baslatmak anlamsiz; sessizce hata vermek yerine acikca duruyoruz.
    foreach ($name in 'backend', 'frontend') {
        $dir = Join-Path $root $name
        if (-not (Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir | Out-Null
            Write-Info "$name/ olusturuldu (bos)"
        }
    }
    Write-Host ''
    Write-Host 'AYAGA KALDIRILAMADI - klasorler hazir ama iceri bos.' -ForegroundColor Red
    Write-Host 'Yapilacaklardan biri:' -ForegroundColor Red
    Write-Host "  * Repo yayinlandiktan sonra bu betigi tekrar calistirin" -ForegroundColor Red
    Write-Host "  * Ya da -RepoUrl ile dogru adresi verin:" -ForegroundColor Red
    Write-Host "      powershell -File .\kurulum.ps1 -RepoUrl <adres>" -ForegroundColor Red
    Write-Host "  * Ya da proje dosyalarini bu klasorlere elle kopyalayin" -ForegroundColor Red
    exit 1
}

Write-Ok "backend  : $($project.Csproj.FullName)"
Write-Ok "frontend : $($project.FrontendDir)"

if ($SkipRun) {
    Write-Host "`n-SkipRun verildi, sunucular baslatilmadi." -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------- 4) Backend
Write-Step "4) Backend baslatiliyor (http://localhost:$ApiPort)"

$backendCmd = "Set-Location '$root'; " +
              "dotnet run --project '$($project.Csproj.FullName)' --no-launch-profile --urls http://localhost:$ApiPort"
Start-Process powershell -ArgumentList '-NoExit', '-Command', $backendCmd | Out-Null

$health = $null
for ($i = 1; $i -le 90; $i++) {
    Start-Sleep -Seconds 1
    try {
        $health = Invoke-RestMethod "http://localhost:$ApiPort/api/health" -TimeoutSec 2
        if ($health.status -eq 'ok') { break }
    } catch { $health = $null }
}

if ($health) {
    Write-Ok "Backend hazir - $($health.indexedDocuments) dokuman indekslendi"
} else {
    Write-Warn2 'Backend 90 saniyede cevap vermedi. Acilan pencerede hata olabilir.'
}

# --------------------------------------------------------------- 5) Frontend
Write-Step '5) Frontend baslatiliyor'

if (-not (Test-Path (Join-Path $project.FrontendDir 'node_modules'))) {
    Write-Info 'npm install calisiyor (ilk kurulumda birkac dakika surebilir)...'
    Push-Location $project.FrontendDir
    try { npm install } finally { Pop-Location }
} else {
    Write-Info 'node_modules mevcut, npm install atlandi'
}

Start-Process powershell -ArgumentList '-NoExit', '-Command', "Set-Location '$($project.FrontendDir)'; npm run dev" | Out-Null
Start-Sleep -Seconds 6

# Vite 5173 mesgulse 5174/5175'e kayar
$uiPort = $null
foreach ($port in 5173, 5174, 5175) {
    try {
        if ((Invoke-WebRequest "http://localhost:$port/" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200) {
            $uiPort = $port
            break
        }
    } catch { }
}

# ------------------------------------------------------------------- Sonuc
Write-Host ''
Write-Host '=========================================================' -ForegroundColor Green
if ($uiPort) {
    Write-Host "  Arayuz : http://localhost:$uiPort" -ForegroundColor Green
} else {
    Write-Warn2 'Arayuz portu tespit edilemedi, frontend penceresine bakin'
}
Write-Host "  API    : http://localhost:$ApiPort/api/health" -ForegroundColor Green
Write-Host '=========================================================' -ForegroundColor Green
Write-Host 'Durdurmak icin acilan iki PowerShell penceresinde Ctrl+C.'

# Tespit edilen adresi kurulum.bat'in okuyabilecegi yere yaz.
# Port tespiti burada yapildigi icin adresi tek kaynaktan uretiyoruz;
# .bat kendi basina port tahmin etmeye calismiyor.
$urlFile = Join-Path $env:TEMP 'dokuman-arsivi-url.txt'
if (Test-Path $urlFile) { Remove-Item $urlFile -Force -ErrorAction SilentlyContinue }
if ($uiPort) {
    Set-Content -Path $urlFile -Value "http://localhost:$uiPort" -Encoding Ascii
    if ($OpenBrowser) {
        Write-Info 'Tarayici aciliyor...'
        Start-Process "http://localhost:$uiPort"
    }
}

exit 0
