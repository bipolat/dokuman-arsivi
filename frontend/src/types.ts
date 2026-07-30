export type DuplicateRef = {
  id: number
  fileName: string
  department: string
  uploadedBy: string
  createdAt: string
  reason: string
}

export type DocumentDto = {
  id: number
  fileName: string
  documentType: string
  department: string
  uploadedBy: string
  sizeBytes: number
  createdAt: string
  sha256: string | null
  snippet: string | null
  score: number
  duplicateCount: number
  duplicates: DuplicateRef[]
  /** İçeriğinden metin çıkarılabildi mi (yani içinde arama yapılabiliyor mu). */
  contentIndexed: boolean
  /** Çıkarılamadıysa sebebi; kullanıcıya gösterilen açıklama. */
  contentNote: string
  /** Diskte açılabilir bir dosyası var mı. */
  fileAvailable: boolean
}

export type FacetBucket = { key: string; count: number }

export type Suggestion = {
  kind:
    | 'clear-filters'
    | 'switch-type'
    | 'switch-department'
    | 'shorten-query'
    | 'narrow-type'
  label: string
  query: string | null
  type: string | null
  department: string | null
}

export type SearchResponse = {
  items: DocumentDto[]
  total: number
  page: number
  pageSize: number
  tookMs: number
  facets: { types: FacetBucket[]; departments: FacetBucket[] }
  messages: string[]
  suggestions: Suggestion[]
  didYouMean: string | null
  collapsedDuplicates: number
  matchesIgnoringFilters: number
}

export type MetaResponse = {
  types: string[]
  departments: string[]
  users: string[]
}

export type PrecheckResponse = {
  verdict: 'new' | 'similar' | 'duplicate'
  message: string
  exactMatches: DocumentDto[]
  similarMatches: DocumentDto[]
}

export type UploadResponse = {
  verdict: 'created' | 'duplicate' | 'similar'
  message: string
  document: DocumentDto | null
  exactMatches: DocumentDto[]
  similarMatches: DocumentDto[]
}

export type ReindexResponse = {
  scanned: number
  extracted: number
  noTextLayer: number
  unsupported: number
  missingFile: number
  tookMs: number
  samples: string[]
}

export type InsightsResponse = {
  documentCount: number
  contentIndexedCount: number
  contentMissingCount: number
  termCount: number
  duplicateClusters: number
  duplicateDocuments: number
  wastedBytes: number
  indexBuiltAt: string
  indexBuildMs: number
  topZeroResultQueries: FacetBucket[]
  topDuplicateClusters: FacetBucket[]
}

export type Filters = {
  q: string
  type: string
  department: string
  from: string
  to: string
  sort: string
  page: number
}
