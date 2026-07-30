import { useState } from 'react'
import { fileUrl } from '../api'
import type { DocumentDto } from '../types'
import { formatBytes, formatDate, relativeDate } from '../utils'
import { Highlight } from './Highlight'

type Props = {
  items: DocumentDto[]
  query: string
  loading: boolean
}

export function DocumentList({ items, query, loading }: Props) {
  if (items.length === 0) return null

  return (
    <ul className={`documents ${loading ? 'documents-stale' : ''}`}>
      {items.map((document) => (
        <DocumentRow key={document.id} document={document} query={query} />
      ))}
    </ul>
  )
}

function DocumentRow({ document, query }: { document: DocumentDto; query: string }) {
  const [expanded, setExpanded] = useState(false)

  return (
    <li className="document">
      <div className="document-main">
        <h3>
          {/* Dokümana tıklamak dosyayı açar. Yeni sekme bilinçli: kullanıcı arama
              sonuçlarını kaybetmeden dokümana bakıp geri dönebiliyor. */}
          {document.fileAvailable ? (
            <a href={fileUrl(document.id)} target="_blank" rel="noreferrer" title="Dokümanı yeni sekmede aç">
              <Highlight text={document.fileName} query={query} />
            </a>
          ) : (
            <span title="Dosya bu ortamda bulunamadı; yalnızca kaydı var.">
              <Highlight text={document.fileName} query={query} />
            </span>
          )}
        </h3>

        <div className="document-meta">
          <span className={`tag tag-${slug(document.documentType)}`}>{document.documentType}</span>
          <span>{document.department}</span>
          <span>{document.uploadedBy}</span>
          <span title={formatDate(document.createdAt)}>{relativeDate(document.createdAt)}</span>
          <span>{formatBytes(document.sizeBytes)}</span>

          {!document.contentIndexed && (
            <span className="tag tag-warn" title={document.contentNote}>
              içerik aranamıyor
            </span>
          )}
          {!document.sha256 && (
            <span
              className="tag tag-warn"
              title="İçerik parmak izi yok: kopya tespiti dosya adı benzerliğine dayanıyor."
            >
              hash yok
            </span>
          )}

          {document.fileAvailable && (
            <a className="meta-action" href={fileUrl(document.id, true)}>
              indir
            </a>
          )}
        </div>

        {document.snippet ? (
          <p className="document-snippet">
            <Highlight text={document.snippet} query={query} />
          </p>
        ) : (
          <p className="document-snippet muted">{document.contentNote}</p>
        )}

        {document.duplicateCount > 0 && (
          <div className="duplicate-block">
            <button type="button" className="link" onClick={() => setExpanded(!expanded)}>
              {expanded ? '▾' : '▸'} Bu dokümanın {document.duplicateCount} kopyası daha var
            </button>
            {expanded && (
              <ul className="duplicate-list">
                {document.duplicates.map((duplicate) => (
                  <li key={duplicate.id}>
                    <a
                      className="duplicate-name"
                      href={fileUrl(duplicate.id)}
                      target="_blank"
                      rel="noreferrer"
                    >
                      {duplicate.fileName}
                    </a>
                    <span className="muted">
                      {duplicate.department} · {duplicate.uploadedBy} ·{' '}
                      {formatDate(duplicate.createdAt)} · {duplicate.reason}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>
    </li>
  )
}

function slug(value: string) {
  return value.toLowerCase().replace('ö', 'o').replace('ş', 's').replace('ı', 'i')
}
