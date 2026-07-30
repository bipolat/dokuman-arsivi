import { useEffect, useRef } from 'react'

type Risk = {
  risk: string
  fix: string
  detail?: string
  /** Neden önemli — yalnızca vurgulanan maddede gösteriliyor. */
  why?: string
  critical?: boolean
}

const notes = [
  'Üç geri bildirim ("bulamıyorum", "tekrar yüklüyorum", "sonuçlar karışık") tek bir soruna işaret ediyor: bir dokümanın sistemde zaten var olup olmadığını makul bir sürede doğrulayamamak. Duplicate hem sonuç hem sebep — bulamayınca yeniden yükleniyor, yüklendikçe bulmak zorlaşıyor. Bu yüzden hem aramaya hem yükleme adımına birlikte dokundum.',
  'Arama yükü veritabanından alınıp uygulama belleğine taşındı: şema değişmedi, yeni bileşen eklenmedi, ölçülen arama p95\'i 0,24 ms (hedef 400 ms idi).',
  'Duplicate kaynağında engelleniyor: SHA-256 tarayıcıda hesaplanıp yüklemeden önce sorgulanıyor, sunucu yükleme anında yeniden doğruluyor. Dosya adı değişse bile tanıyor.',
  'İçerik metni PDF, Office (OOXML), OpenDocument, RTF, HTML ve düz metin ailesinden çıkarılıp indeksleniyor. Taranmış belge ve eski ikili Office kapsam dışı — sebebi sonuç listesinde etiketle görünüyor.',
  'Sıralama BM25 + alan ağırlıkları + yenilik çarpanı. Ağırlıkların hiçbiri veriyle doğrulanmadı; projede beni en rahatsız eden nokta bu.',
  'Ölçmediğim şeyi iyileştiremeyeceğim için sonuçsuz arama sayacı ve kopya istatistikleri baştan açık tutuldu.',
]

const schema = `React  (arama · filtre · upload)
   │   HTTP
   ▼
ASP.NET Core API  ────── tek süreç
   ├── RAM ters indeks + BM25        ← aramalar yalnızca buradan
   ├── DocumentService  (hash · duplicate · metin çıkarımı)
   └── Repository       (SELECT / INSERT)
         │
         ├──►  Documents tablosu        ŞEMA DEĞİŞMEDİ
         ├──►  sidecar-hashes.jsonl     dokümanId → hash
         └──►  blobs/<sha256>           içerik adresli depolama

Açılış : tablo bir kez okunur → RAM indeksi kurulur
Arama  : veritabanına gitmez, yalnızca RAM`

const risks: Risk[] = [
  {
    risk: 'Upload süresi uzadığında kullanıcının aradığını bulamaması',
    fix: 'İki kademeli sorgu havuzu',
    detail:
      'Taze yüklenen kayıtlar, ana havuza işlenene kadar küçük bir ön havuzda barındırılır. Sorgu önce bu havuza bakar (düşük maliyet), ardından zaten var olan garanti veri havuzundan diğer sonuçları getirir. Böylece indeksleme mekanizmalarının en büyük dezavantajı olan insert gecikmesi minimize edilir.',
    critical: true,
    why: 'Kullanıcı yüklediği dokümanı bir sonraki aramada bulamazsa hata mesajı da almaz — sistemin çalışmadığını sanır ve yeniden yükler. Yani çözmeye çalıştığımız duplicate problemi, indeksleme gecikmesi yüzünden yeniden üretilir. Bu yüzden listedeki teknik olarak en küçük, kullanıcı deneyimi açısından en kritik madde.',
  },
  {
    risk: 'T0 anında verinin peşin olarak RAM\'e aktarılmasından kaynaklı bekleme süresi',
    fix: 'Planlı deploy',
  },
  {
    risk: 'Arşiv büyüdükçe upload maliyetinin artması',
    fix: 'Asenkron yükleme · Lucene.NET · RAG / vektörel DB',
  },
]

/** Her madde "ne" + "neden" olarak yazıldı: özellik listesi değil, problem-çözüm eşleşmesi. */
const improvements: { what: string; why: string }[] = [
  {
    what: 'Aynı içerikli dosya farklı tür ve isimde eklenmek istendiğinde dosya birleştirme seçeneği',
    why: 'Aynı sözleşmenin PDF ve DOCX hali farklı hash üretiyor; hash eşitliği bunları yakalayamıyor. Birleştirme, kullanıcının "bunlar aynı doküman" kararını sisteme kalıcı olarak öğretir.',
  },
  {
    what: 'Verimi artırmak adına kademeli arama (maliyeti düşükten yükseğe)',
    why: 'Sorguların büyük kısmı dosya adı eşleşmesiyle çözülüyor. Pahalı içerik taramasını yalnızca ucuz kademe yetersiz kaldığında çalıştırmak ortalama maliyeti düşürür.',
  },
  {
    what: 'Semantik arama — anlamsal ilişkileri yakalamak için',
    why: 'Kullanıcı "kira artışı" arıyor, dokümanda "TÜFE oranında güncelleme" yazıyor. Kelime eşleşmesi bunu bulamaz, anlamsal yakınlık bulur.',
  },
  {
    what: 'Tooltip ile evrak ön izlemesi',
    why: 'Doğru dokümanı bulmak için hâlâ tek tek açmak gerekiyor. Üzerine gelince ilk satırların görünmesi "aç–bak–geri dön" turunu ortadan kaldırır.',
  },
  {
    what: 'Metadata araması',
    why: '"Fat 12" yazıldığında 12*** ile başlayan fatura numaralarını listelemek. Kullanıcı çoğu zaman numaranın tamamını değil baş kısmını hatırlıyor.',
  },
  {
    what: 'OCR — ekran görüntüsü, fotoğraf gibi içeriklerin desteklenmesi',
    why: 'Taranmış belgelerin içi şu an tamamen aranamıyor; arşivin bu kısmına yalnızca dosya adıyla erişilebiliyor.',
  },
  {
    what: 'Arama metrik kriterlerini dinamik değiştirebilen modlar',
    why: 'Alan ağırlıkları, BM25 parametreleri ve eşikler şu an kodda sabit ve doğrulanmamış. Ayarlanabilir olsalar dönemsel işlemlere göre (fatura dönemi, sözleşme yenileme sezonu) kalibre edilip performans kazanılabilir.',
  },
]

export function InfoModal({ onClose }: { onClose: () => void }) {
  const closeRef = useRef<HTMLButtonElement>(null)

  // Escape ile kapatma: modal açıkken kullanıcının kaçış yolu her zaman olmalı.
  useEffect(() => {
    closeRef.current?.focus()
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div className="modal-backdrop" onClick={onClose}>
      {/* Kartın içine tıklamak kapatmasın */}
      <div
        className="modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="info-title"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="modal-head">
          <h2 id="info-title">Proje notları</h2>
          <button ref={closeRef} type="button" className="ghost modal-close" onClick={onClose}>
            Kapat
          </button>
        </div>

        <div className="modal-body">
          <p className="modal-note">
            <strong>Not:</strong> Tüm proje ek altyapı maliyeti olmayacak şekilde düşünüldü. Buraya
            beni heyecanlandıran bir iki madde de ekledim.
          </p>

          <section className="modal-section">
            <h3>Özet ve kendimden notlar</h3>
            <ul>
              {notes.map((note) => (
                <li key={note}>{note}</li>
              ))}
            </ul>
          </section>

          <section className="modal-section">
            <h3>Sistemin basit bir şeması</h3>
            <pre className="schema">{schema}</pre>
          </section>

          <section className="modal-section">
            <h3>Alınan riskler ve çözümler</h3>
            <ul className="risk-list">
              {risks.map((item) => (
                <li key={item.risk} className={item.critical ? 'risk-critical' : undefined}>
                  {item.critical && (
                    <span className="risk-badge">
                      ★ Kullanıcı konforu açısından en önemli nokta
                    </span>
                  )}
                  <span className="risk-text">{item.risk}</span>
                  <span className="risk-fix">→ {item.fix}</span>
                  {item.detail && <span className="risk-detail">{item.detail}</span>}
                  {item.why && (
                    <span className="risk-why">
                      <strong>Neden kritik:</strong> {item.why}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </section>

          <section className="modal-section">
            <h3>Yapılabilecek iyileştirmeler</h3>
            <ul className="improvement-list">
              {improvements.map((item) => (
                <li key={item.what}>
                  <span className="imp-what">{item.what}</span>
                  <span className="imp-why">{item.why}</span>
                </li>
              ))}
            </ul>
          </section>
        </div>
      </div>
    </div>
  )
}
