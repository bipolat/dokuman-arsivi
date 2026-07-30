export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export function formatDate(iso: string): string {
  const date = new Date(iso)
  return date.toLocaleDateString('tr-TR', { day: '2-digit', month: '2-digit', year: 'numeric' })
}

export function relativeDate(iso: string): string {
  const days = Math.floor((Date.now() - new Date(iso).getTime()) / 86_400_000)
  if (days <= 0) return 'bugün'
  if (days === 1) return 'dün'
  if (days < 30) return `${days} gün önce`
  if (days < 365) return `${Math.floor(days / 30)} ay önce`
  return `${Math.floor(days / 365)} yıl önce`
}

/** Backend'in kullandığı Türkçe katlama kuralının tarayıcı kopyası (sadece görsel vurgu için). */
const foldMap: Record<string, string> = {
  ı: 'i', İ: 'i', I: 'i', ş: 's', Ş: 's', ğ: 'g', Ğ: 'g',
  ü: 'u', Ü: 'u', ö: 'o', Ö: 'o', ç: 'c', Ç: 'c',
}

export function fold(value: string): string {
  return value
    .split('')
    .map((c) => foldMap[c] ?? c.toLowerCase())
    .join('')
}

/** Eşleşen kelimeleri parçalara böler; React tarafında <mark> ile sarılır. */
export function highlightParts(text: string, query: string): { text: string; hit: boolean }[] {
  if (!text) return []
  const tokens = fold(query)
    .split(/[^\p{L}\p{N}]+/u)
    .filter((t) => t.length >= 2)
  if (tokens.length === 0) return [{ text, hit: false }]

  const folded = fold(text)
  const marks = new Array<boolean>(text.length).fill(false)

  for (const token of tokens) {
    let index = folded.indexOf(token)
    while (index >= 0) {
      for (let i = index; i < index + token.length; i++) marks[i] = true
      index = folded.indexOf(token, index + token.length)
    }
  }

  const parts: { text: string; hit: boolean }[] = []
  let start = 0
  for (let i = 1; i <= text.length; i++) {
    if (i === text.length || marks[i] !== marks[start]) {
      parts.push({ text: text.slice(start, i), hit: marks[start] })
      start = i
    }
  }
  return parts
}
