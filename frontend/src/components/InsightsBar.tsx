import { useState } from 'react'
import { reindexContent } from '../api'
import type { InsightsResponse, ReindexResponse } from '../types'
import { InfoModal } from './InfoModal'

/**
 * Ölçüm olmadan "duplicate azaldı mı, arama iyileşti mi?" sorusuna cevap veremeyiz.
 * Bu şerit, çözümün etkisini takip etmek için minimum göstergeleri açıyor.
 */
export function InsightsBar({
  insights,
  onReindexed,
}: {
  insights: InsightsResponse | null
  onReindexed: () => void
}) {
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<ReindexResponse | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [infoOpen, setInfoOpen] = useState(false)

  if (!insights) return null

  async function runReindex() {
    setBusy(true)
    setError(null)
    try {
      setResult(await reindexContent())
      onReindexed()
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Yeniden indeksleme başarısız.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <section aria-label="Sistem göstergeleri">
      <div className="insights">
        <Stat label="Doküman" value={insights.documentCount.toLocaleString('tr-TR')} />
        <Stat
          label="İçeriği aranabilir"
          value={`${insights.contentIndexedCount} / ${insights.documentCount}`}
          title="Metni çıkarılmış, yani içinde arama yapılabilen doküman sayısı."
        />
        <Stat label="Kopya grubu" value={String(insights.duplicateClusters)} />
        <Stat label="Fazla kopya" value={String(insights.duplicateDocuments)} />
        <Stat label="İndeks kurulumu" value={`${insights.indexBuildMs} ms`} />
        <Stat
          label="Sonuçsuz arama"
          value={String(insights.topZeroResultQueries.reduce((sum, q) => sum + q.count, 0))}
          title={
            insights.topZeroResultQueries.length > 0
              ? insights.topZeroResultQueries.map((q) => `${q.key} (${q.count})`).join(', ')
              : 'Henüz sonuçsuz arama yok'
          }
        />

        {insights.contentMissingCount > 0 && (
          <button type="button" className="ghost stat-action" disabled={busy} onClick={runReindex}>
            {busy
              ? 'Metinler çıkarılıyor…'
              : `${insights.contentMissingCount} dokümanın içeriğini çıkar`}
          </button>
        )}

        {/* Satırın en sağı: proje notları, riskler ve iyileştirme fikirleri */}
        <button
          type="button"
          className="info-button"
          title="Proje notları, alınan riskler ve iyileştirme fikirleri"
          aria-label="Proje notları"
          onClick={() => setInfoOpen(true)}
        >
          i
        </button>
      </div>

      {infoOpen && <InfoModal onClose={() => setInfoOpen(false)} />}

      {error && <p className="feedback feedback-error">{error}</p>}

      {result && (
        <div className="feedback feedback-ok">
          <strong>İçerik çıkarımı tamamlandı</strong> ({result.tookMs} ms) — {result.scanned} doküman
          tarandı: <b>{result.extracted}</b> metni çıkarıldı, {result.noTextLayer} metin katmanı yok,{' '}
          {result.unsupported} biçim desteklenmiyor, {result.missingFile} dosyası bulunamadı.
          {result.samples.length > 0 && (
            <ul className="reindex-samples">
              {result.samples.map((sample) => (
                <li key={sample}>{sample}</li>
              ))}
            </ul>
          )}
        </div>
      )}
    </section>
  )
}

function Stat({ label, value, title }: { label: string; value: string; title?: string }) {
  return (
    <div className="stat" title={title}>
      <span className="stat-value">{value}</span>
      <span className="stat-label">{label}</span>
    </div>
  )
}
