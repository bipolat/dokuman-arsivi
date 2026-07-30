import type {
  Filters,
  InsightsResponse,
  MetaResponse,
  PrecheckResponse,
  ReindexResponse,
  SearchResponse,
  UploadResponse,
} from './types'

const base = '/api'

async function json<T>(response: Response): Promise<T> {
  if (!response.ok && response.status !== 409) {
    const text = await response.text()
    throw new Error(text || `İstek başarısız (${response.status})`)
  }
  return (await response.json()) as T
}

export function searchDocuments(filters: Filters, signal?: AbortSignal) {
  const params = new URLSearchParams()
  if (filters.q.trim()) params.set('q', filters.q)
  if (filters.type) params.set('type', filters.type)
  if (filters.department) params.set('department', filters.department)
  if (filters.from) params.set('from', filters.from)
  if (filters.to) params.set('to', `${filters.to}T23:59:59`)
  if (filters.sort) params.set('sort', filters.sort)
  params.set('page', String(filters.page))
  params.set('pageSize', '20')

  return fetch(`${base}/documents?${params}`, { signal }).then(json<SearchResponse>)
}

export function getMeta() {
  return fetch(`${base}/meta`).then(json<MetaResponse>)
}

export function getInsights() {
  return fetch(`${base}/insights`).then(json<InsightsResponse>)
}

export function reindexContent() {
  return fetch(`${base}/admin/reindex-content`, { method: 'POST' }).then(json<ReindexResponse>)
}

/** Dokümanı açma (yeni sekmede) veya indirme adresi. */
export function fileUrl(id: number, download = false) {
  return `${base}/documents/${id}/file${download ? '?download=1' : ''}`
}

export function precheck(fileName: string, sizeBytes: number, sha256: string | null) {
  return fetch(`${base}/documents/precheck`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ fileName, sizeBytes, sha256 }),
  }).then(json<PrecheckResponse>)
}

export function uploadDocument(
  file: File,
  documentType: string,
  department: string,
  uploadedBy: string,
  force: boolean,
) {
  const form = new FormData()
  form.append('file', file)
  form.append('documentType', documentType)
  form.append('department', department)
  form.append('uploadedBy', uploadedBy)
  form.append('force', String(force))

  return fetch(`${base}/documents`, { method: 'POST', body: form }).then(json<UploadResponse>)
}

/**
 * Dosyanın SHA-256'sını tarayıcıda hesaplar.
 * Amaç: byte'lar sunucuya gitmeden "bu doküman zaten var" diyebilmek.
 * Sunucu bu değere güvenmez, yüklemede kendisi yeniden hesaplar.
 */
export async function hashFile(file: File): Promise<string | null> {
  if (!crypto?.subtle) return null // güvenli olmayan origin: precheck'i atla, sunucu yine yakalar
  const buffer = await file.arrayBuffer()
  const digest = await crypto.subtle.digest('SHA-256', buffer)
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('')
}
