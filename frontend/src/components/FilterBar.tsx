import type { Filters, MetaResponse, SearchResponse } from '../types'

type Props = {
  filters: Filters
  meta: MetaResponse | null
  result: SearchResponse | null
  onChange: (patch: Partial<Filters>) => void
  onReset: () => void
}

const sortLabels: Record<string, string> = {
  relevance: 'İlgi düzeyi',
  newest: 'En yeni',
  oldest: 'En eski',
  name: 'Dosya adı',
  size: 'Boyut',
}

export function FilterBar({ filters, meta, result, onChange, onReset }: Props) {
  const activeCount =
    (filters.type ? 1 : 0) +
    (filters.department ? 1 : 0) +
    (filters.from ? 1 : 0) +
    (filters.to ? 1 : 0)

  return (
    <section className="filters" aria-label="Filtreler">
      <div className="filter-row">
        <label>
          <span>Tür</span>
          <select value={filters.type} onChange={(e) => onChange({ type: e.target.value, page: 1 })}>
            <option value="">Tümü</option>
            {meta?.types.map((type) => (
              <option key={type} value={type}>
                {type}
              </option>
            ))}
          </select>
        </label>

        <label>
          <span>Departman</span>
          <select
            value={filters.department}
            onChange={(e) => onChange({ department: e.target.value, page: 1 })}
          >
            <option value="">Tümü</option>
            {meta?.departments.map((department) => (
              <option key={department} value={department}>
                {department}
              </option>
            ))}
          </select>
        </label>

        <label>
          <span>Başlangıç</span>
          <input
            type="date"
            value={filters.from}
            onChange={(e) => onChange({ from: e.target.value, page: 1 })}
          />
        </label>

        <label>
          <span>Bitiş</span>
          <input
            type="date"
            value={filters.to}
            onChange={(e) => onChange({ to: e.target.value, page: 1 })}
          />
        </label>

        <label>
          <span>Sıralama</span>
          <select value={filters.sort} onChange={(e) => onChange({ sort: e.target.value, page: 1 })}>
            {Object.entries(sortLabels).map(([value, label]) => (
              <option key={value} value={value}>
                {label}
              </option>
            ))}
          </select>
        </label>

        {activeCount > 0 && (
          <button type="button" className="ghost" onClick={onReset}>
            Filtreleri temizle ({activeCount})
          </button>
        )}
      </div>

      {/* Facet'ler tür/departman filtresi uygulanmadan sayılır: kullanıcı sonucun
          hangi departmanda olduğunu filtreyi denemeden görebiliyor. */}
      {result && result.facets.departments.length > 1 && (
        <div className="facets">
          <span className="facet-label">Departmana göre:</span>
          {result.facets.departments.map((bucket) => (
            <button
              key={bucket.key}
              type="button"
              className={`chip ${filters.department === bucket.key ? 'chip-active' : ''}`}
              onClick={() =>
                onChange({
                  department: filters.department === bucket.key ? '' : bucket.key,
                  page: 1,
                })
              }
            >
              {bucket.key} <b>{bucket.count}</b>
            </button>
          ))}
        </div>
      )}
    </section>
  )
}
