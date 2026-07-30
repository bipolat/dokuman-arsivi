import { useCallback, useEffect, useState } from 'react'
import { getInsights, getMeta, searchDocuments } from './api'
import { DocumentList } from './components/DocumentList'
import { FeedbackPanel } from './components/FeedbackPanel'
import { FilterBar } from './components/FilterBar'
import { InsightsBar } from './components/InsightsBar'
import { UploadPanel } from './components/UploadPanel'
import type { Filters, InsightsResponse, MetaResponse, SearchResponse } from './types'
import './styles.css'

const emptyFilters: Filters = {
  q: '',
  type: '',
  department: '',
  from: '',
  to: '',
  sort: 'relevance',
  page: 1,
}

export default function App() {
  const [filters, setFilters] = useState<Filters>(emptyFilters)
  const [result, setResult] = useState<SearchResponse | null>(null)
  const [meta, setMeta] = useState<MetaResponse | null>(null)
  const [insights, setInsights] = useState<InsightsResponse | null>(null)
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [reloadToken, setReloadToken] = useState(0)

  useEffect(() => {
    getMeta()
      .then(setMeta)
      .catch(() => setMeta(null))
  }, [])

  const refreshInsights = useCallback(() => {
    getInsights()
      .then(setInsights)
      .catch(() => setInsights(null))
  }, [])

  useEffect(() => refreshInsights(), [refreshInsights, reloadToken])

  // Yazarken arama: her tuş vuruşu istek atmasın. 8.000 kullanıcı × her harf =
  // gereksiz yük; 250 ms debounce istek sayısını belirgin şekilde düşürüyor.
  useEffect(() => {
    const controller = new AbortController()
    const timer = setTimeout(() => {
      setLoading(true)
      searchDocuments(filters, controller.signal)
        .then((response) => {
          setResult(response)
          setError(null)
        })
        .catch((e: unknown) => {
          if (e instanceof DOMException && e.name === 'AbortError') return
          setError(e instanceof Error ? e.message : 'Bilinmeyen hata')
        })
        .finally(() => setLoading(false))
    }, 250)

    return () => {
      clearTimeout(timer)
      controller.abort()
    }
  }, [filters, reloadToken])

  const patch = useCallback((changes: Partial<Filters>) => {
    setFilters((current) => ({ ...current, ...changes }))
  }, [])

  const reset = useCallback(() => {
    setFilters((current) => ({ ...emptyFilters, q: current.q }))
  }, [])

  const totalPages = result ? Math.max(1, Math.ceil(result.total / result.pageSize)) : 1

  return (
    <div className="app">
      <header className="app-header">
        <h1>Doküman Arşivi</h1>
        <p className="muted">
          Sözleşme, teklif ve fatura arama · mevcut veritabanı değişmeden, RAM içi indeks üzerinden
        </p>
      </header>

      <InsightsBar insights={insights} onReindexed={() => setReloadToken((token) => token + 1)} />

      <div className="search-box">
        <input
          type="search"
          value={filters.q}
          placeholder="Doküman adı, tedarikçi, departman veya kişi ara…"
          onChange={(e) => patch({ q: e.target.value, page: 1 })}
          autoFocus
        />
        {filters.q && (
          <button type="button" className="ghost" onClick={() => patch({ q: '', page: 1 })}>
            Temizle
          </button>
        )}
      </div>

      <FilterBar filters={filters} meta={meta} result={result} onChange={patch} onReset={reset} />

      <main className="layout">
        <div className="results">
          <FeedbackPanel
            result={result}
            loading={loading}
            error={error}
            filters={filters}
            onChange={patch}
            onReset={reset}
          />

          <DocumentList items={result?.items ?? []} query={filters.q} loading={loading} />

          {result && result.total > result.pageSize && (
            <nav className="pager">
              <button
                type="button"
                disabled={filters.page <= 1}
                onClick={() => patch({ page: filters.page - 1 })}
              >
                ‹ Önceki
              </button>
              <span className="muted">
                {filters.page} / {totalPages}
              </span>
              <button
                type="button"
                disabled={filters.page >= totalPages}
                onClick={() => patch({ page: filters.page + 1 })}
              >
                Sonraki ›
              </button>
            </nav>
          )}
        </div>

        <aside className="sidebar">
          <UploadPanel
            meta={meta}
            onUploaded={() => setReloadToken((token) => token + 1)}
            onSearchExisting={(query) => setFilters({ ...emptyFilters, q: query })}
          />
        </aside>
      </main>
    </div>
  )
}
