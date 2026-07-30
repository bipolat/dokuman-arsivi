# Kurulum ve Ayağa Kaldırma

İki dosya bu işi yapıyor:

| Dosya | Görevi |
|---|---|
| **`kurulum.bat`** | Gereksinimleri kontrol eder, eksikleri **winget ile kurar**, sonra `kurulum.ps1`'i çağırır |
| **`kurulum.ps1`** | Klasörleri kontrol eder, repoyu çeker, backend + frontend'i ayağa kaldırır |

İkisi de **bulunduğu klasörü proje kökü kabul eder.** Bilgisayarda nereye koyarsanız
(`C:\projeler\arsiv`, `D:\work`, masaüstü — fark etmez) o klasöre göre çalışır. Sabit yol yok.

## Kullanım

`kurulum.bat` dosyasına **çift tıklayın.** Hepsi bu.

Ya da terminalden:

```powershell
.\kurulum.bat
```

Sadece PowerShell tarafını çalıştırmak isterseniz (gereksinimler zaten kuruluysa):

```powershell
powershell -ExecutionPolicy Bypass -File .\kurulum.ps1
```

---

## Akış

```
kurulum.bat
   │
   ├─ [1/3] Gereksinim kontrolü      dotnet · node · git  →  eksikse winget ile kur
   ├─ [2/3] Doğrulama                PATH yenile, tekrar kontrol et
   ├─ [3/3] kurulum.ps1'i çağır
   │          │
   │          ├─ 1) backend/ ve frontend/ klasörleri var mı, içlerinde proje var mı?
   │          ├─ 2) Yoksa repoyu çek
   │          ├─ 3) KAPI: proje gerçekten var mı? — yoksa anlaşılır mesajla dur
   │          ├─ 4) Backend'i ayağa kaldır    → http://localhost:5099
   │          ├─ 5) Frontend'i ayağa kaldır   → http://localhost:5173
   │          └─ Tespit edilen adresi geçici bir dosyaya yaz
   │
   └─ Adresi okuyup TARAYICIYI AÇ
```

Adres tespiti `kurulum.ps1` tarafında yapılıyor (Vite 5173 doluysa 5174'e kayabiliyor),
`.bat` port tahmin etmiyor — ps1'in yazdığı adresi okuyup açıyor. Böylece port bilgisi tek
kaynaktan geliyor.

`kurulum.ps1`'i tek başına çalıştırıp tarayıcının da açılmasını isterseniz:

```powershell
powershell -ExecutionPolicy Bypass -File .\kurulum.ps1 -OpenBrowser
```

### 1. Gereksinimler (`kurulum.bat`)

| Araç | winget paketi | Neden |
|---|---|---|
| .NET SDK | `Microsoft.DotNet.SDK.10` | Proje `net10.0` hedefliyor |
| Node.js | `OpenJS.NodeJS.LTS` | Frontend build/dev sunucusu |
| Git | `Git.Git` | Repo çekimi |

Her araç için önce `where` ile varlık kontrolü yapılır, varsa sürümü yazdırılır, yoksa winget ile
sessiz kurulum denenir. **UAC izin penceresi çıkabilir, onaylamanız gerekir.**

Kurulum yapıldıysa PATH bu oturuma yeniden yüklenir — yoksa yeni kurulan araç "bulunamadı"
görünürdü. Buna rağmen bulunamazsa script *"bu pencereyi kapatıp tekrar çalıştırın"* diyerek durur.

winget yoksa (Windows 10'un eski sürümleri) otomatik kurulum yapılamaz; script bunu söyler ve
indirme adreslerini verir.

### 2. Klasör ve repo (`kurulum.ps1`)

Klasörün **varlığı yeterli sayılmıyor**, içinde gerçek proje aranıyor:

- `backend/` altında (alt klasörler dahil) herhangi bir `.csproj`
- `frontend/package.json`

Proje yoksa repo çekilir. Repo geçici bir klasöre çekilip içeriği köke taşınır — çünkü `git clone`
dolu bir klasöre doğrudan çekemez, bu yöntemle kökte `kurulum.bat` gibi dosyalar olsa bile çalışır.

Varsayılan kaynak: **https://github.com/bipolat/dokuman-arsivi** (public — kimlik doğrulaması
gerekmez, herkes çekebilir).

Farklı bir adresten çekmek için:

```powershell
powershell -File .\kurulum.ps1 -RepoUrl https://github.com/<hesap>/<repo>.git
```

### 3. Kapı kontrolü

Bu adım bilinçli: 2. adım başarısız olduysa klasörler **boş** kalmış olabilir. Boş klasörü ayağa
kaldırmaya çalışmak anlaşılmaz hata verir. Onun yerine `backend/` ve `frontend/` oluşturulur,
durum açıkça yazılır ve çıkılır:

```
AYAGA KALDIRILAMADI - klasorler hazir ama iceri bos.
```

### 4-5. Sunucular

Backend ayrı bir pencerede 5099 portunda başlar; `/api/health` cevap verene kadar (en fazla 90 sn)
beklenir. **İlk çalıştırmada** demo verisi otomatik oluşur (60 doküman + gerçek
PDF/DOCX/XLSX/ODT/RTF/HTML dosyaları), o yüzden ilk açılış birkaç saniye uzun sürer.

Frontend'de `node_modules` yoksa `npm install` çalışır (varsa atlanır), sonra ayrı bir pencerede
`npm run dev` başlar. Port 5173 doluysa Vite kendiliğinden 5174'e kayar; script üç portu yoklayıp
doğru adresi yazdırır.

> **5099 portu önemli:** `frontend/vite.config.ts`, `/api` isteklerini `http://localhost:5099`
> adresine proxy'liyor. Backend başka portta çalışırsa arayüz boş görünür. Portu değiştirirseniz
> ikisini birlikte değiştirin (`-ApiPort` parametresi + vite config).

---

## Doğrulama

```powershell
# API cevap veriyor mu
Invoke-RestMethod 'http://localhost:5099/api/health'
# beklenen: status=ok, indexedDocuments=60

# Türkçe katlama çalışıyor mu
(Invoke-RestMethod 'http://localhost:5099/api/documents?q=sozlesme').total
# beklenen: 20+

# 23 senaryoluk tam test (API ayaktayken)
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-test.ps1
```

Arayüzde görmeniz gerekenler: gösterge şeridi, arama kutusu, filtreler, doküman listesi, sağda
yükleme paneli, gösterge şeridinin **en sağında `i` butonu** (proje notları popup'ı).

---

## Sorun giderme

| Belirti | Çözüm |
|---|---|
| `.bat` açılıp hemen kapanıyor | Terminalden çalıştırın, hata mesajı görünür: `.\kurulum.bat` |
| Kurulum sonrası "araç bulunamadı" | PATH henüz geçerli değil. Pencereyi kapatıp `.bat`'ı tekrar çalıştırın |
| winget bulunamadı | Microsoft Store → "App Installer" kurun, ya da araçları elle indirin |
| Backend açılıp kapanıyor | 5099 meşgul: `Get-NetTCPConnection -LocalPort 5099` |
| Arayüz açılıyor ama liste boş | Backend 5099'da değil → `Invoke-RestMethod http://localhost:5099/api/health` |
| `npm install` hata veriyor | `frontend/node_modules` ve `package-lock.json` silinip tekrar denenir |
| Demo verisini sıfırlama | `backend/DocArchive.Api/storage` klasörünü silin, backend'i yeniden başlatın. Kilitli derse editörü kapatıp tekrar deneyin |
| Türkçe karakterler bozuk | `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8` |

## Notlar

- Her iki script **idempotent**: birden fazla çalıştırılabilir. Var olan klasörlere, mevcut
  `node_modules`'a ve oluşmuş demo verisine dokunmaz.
- Sunucular **ayrı pencerelerde** (`-NoExit`) açılır; logları görebilir, `Ctrl+C` ile durdurabilirsiniz.
- Tek süreçte çalıştırmak isterseniz: `cd frontend; npm run build` → çıktı API'nin `wwwroot`
  klasörüne gider, sonra yalnızca **http://localhost:5099** yeterlidir.
- Sunucu başlatmadan sadece klasör/repo hazırlığı için: `.\kurulum.ps1 -SkipRun`
