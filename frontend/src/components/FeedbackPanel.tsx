import type { Filters, SearchResponse } from '../types'

type Props = {
  result: SearchResponse | null
  loading: boolean
  error: string | null
  filters: Filters
  onChange: (patch: Partial<Filters>) => void
  onReset: () => void
}

/**
 * "Sonuç bulunamadı" tek başına bir geri bildirim değil, çıkmaz sokaktır.
 * Bu panel her boş sonuçta en az bir sonraki adım öneriyor.
 */
export function FeedbackPanel({ result, loading, error, filters, onChange, onReset }: Props) {
  if (error) {
    return (
      <div className="feedback feedback-error" role="alert">
        <strong>Arama yapılamadı.</strong> {error}
      </div>
    )
  }

  if (!result) {
    return <div className="feedback feedback-muted">{loading ? 'Aranıyor…' : 'Yükleniyor…'}</div>
  }

  const summary =
    result.total === 0
      ? 'Sonuç yok'
      : `${result.total} sonuç` +
        (result.collapsedDuplicates > 0 ? ` (+${result.collapsedDuplicates} kopya gruplandı)` : '')

  return (
    <div className={`feedback ${result.total === 0 ? 'feedback-empty' : ''}`} aria-live="polite">
      <div className="feedback-head">
        <strong>{summary}</strong>
        <span className="muted">
          {result.tookMs} ms · sayfa {result.page}
          {loading ? ' · güncelleniyor…' : ''}
        </span>
      </div>

      {result.messages.map((message) => (
        <p key={message} className="feedback-message">
          {message}
        </p>
      ))}

      {result.didYouMean && (
        <p className="feedback-message">
          Şunu mu demek istediniz:{' '}
          <button
            type="button"
            className="link"
            onClick={() => onChange({ q: result.didYouMean!, page: 1 })}
          >
            {result.didYouMean}
          </button>
        </p>
      )}

      {result.suggestions.length > 0 && (
        <div className="suggestions">
          {result.suggestions.map((suggestion, index) => (
            <button
              key={`${suggestion.kind}-${index}`}
              type="button"
              className="chip chip-suggestion"
              onClick={() => {
                if (suggestion.kind === 'clear-filters') {
                  onReset()
                  return
                }
                onChange({
                  page: 1,
                  ...(suggestion.query !== null ? { q: suggestion.query } : {}),
                  ...(suggestion.type !== null ? { type: suggestion.type } : {}),
                  ...(suggestion.department !== null ? { department: suggestion.department } : {}),
                })
              }}
            >
              {suggestion.label}
            </button>
          ))}
        </div>
      )}

      {result.total === 0 && filters.q.trim().length > 0 && result.suggestions.length === 0 && (
        <p className="feedback-message muted">
          İpucu: doküman adının bir parçası, tedarikçi adı veya yükleyen kişinin kullanıcı adı da
          çalışır.
        </p>
      )}
    </div>
  )
}
