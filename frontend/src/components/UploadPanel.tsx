import { useRef, useState } from 'react'
import { hashFile, precheck, uploadDocument } from '../api'
import type { DocumentDto, MetaResponse, PrecheckResponse, UploadResponse } from '../types'
import { formatDate } from '../utils'

type Props = {
  meta: MetaResponse | null
  onUploaded: () => void
  onSearchExisting: (query: string) => void
}

/**
 * Duplicate azaltma mekanizmasının kullanıcıya dönük yarısı.
 * Sıra önemli: dosya seçildiği anda (yükleme başlamadan) hash hesaplanıp sorulur.
 * Kullanıcı "aynısı zaten var" bilgisini, yükleme bittikten sonra değil, öncesinde görür.
 */
export function UploadPanel({ meta, onUploaded, onSearchExisting }: Props) {
  const [file, setFile] = useState<File | null>(null)
  const [hash, setHash] = useState<string | null>(null)
  const [check, setCheck] = useState<PrecheckResponse | null>(null)
  const [checking, setChecking] = useState(false)
  const [result, setResult] = useState<UploadResponse | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [documentType, setDocumentType] = useState('Sözleşme')
  const [department, setDepartment] = useState('Hukuk')
  const [uploadedBy, setUploadedBy] = useState('ayse.demir')
  const inputRef = useRef<HTMLInputElement>(null)

  function reset() {
    setFile(null)
    setHash(null)
    setCheck(null)
    setResult(null)
    setError(null)
    if (inputRef.current) inputRef.current.value = ''
  }

  async function onSelect(selected: File | null) {
    setCheck(null)
    setResult(null)
    setError(null)
    setFile(selected)
    setHash(null)
    if (!selected) return

    setChecking(true)
    try {
      const computed = await hashFile(selected)
      setHash(computed)
      setCheck(await precheck(selected.name, selected.size, computed))
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Ön kontrol yapılamadı.')
    } finally {
      setChecking(false)
    }
  }

  async function submit(force: boolean) {
    if (!file) return
    setBusy(true)
    setError(null)
    try {
      const response = await uploadDocument(file, documentType, department, uploadedBy, force)
      setResult(response)
      if (response.verdict === 'created') {
        onUploaded()
        setFile(null)
        setHash(null)
        setCheck(null)
        if (inputRef.current) inputRef.current.value = ''
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Yükleme başarısız.')
    } finally {
      setBusy(false)
    }
  }

  const blocked = check?.verdict === 'duplicate' || result?.verdict === 'duplicate'
  const warned = check?.verdict === 'similar' || result?.verdict === 'similar'
  const matches = [
    ...(result?.exactMatches ?? check?.exactMatches ?? []),
    ...(result?.similarMatches ?? check?.similarMatches ?? []),
  ]

  return (
    <section className="upload" aria-label="Doküman yükle">
      <h2>Doküman yükle</h2>

      <div className="upload-fields">
        <label>
          <span>Dosya</span>
          <input
            ref={inputRef}
            type="file"
            onChange={(e) => onSelect(e.target.files?.[0] ?? null)}
          />
        </label>
        <label>
          <span>Tür</span>
          <select value={documentType} onChange={(e) => setDocumentType(e.target.value)}>
            {(meta?.types ?? ['Sözleşme', 'Teklif', 'Fatura']).map((type) => (
              <option key={type}>{type}</option>
            ))}
          </select>
        </label>
        <label>
          <span>Departman</span>
          <select value={department} onChange={(e) => setDepartment(e.target.value)}>
            {(meta?.departments ?? ['Hukuk']).map((item) => (
              <option key={item}>{item}</option>
            ))}
          </select>
        </label>
        <label>
          <span>Yükleyen</span>
          <select value={uploadedBy} onChange={(e) => setUploadedBy(e.target.value)}>
            {(meta?.users ?? ['ayse.demir']).map((user) => (
              <option key={user}>{user}</option>
            ))}
          </select>
        </label>
      </div>

      {checking && <p className="feedback-muted">Dosya parmak izi hesaplanıyor…</p>}

      {file && hash === null && !checking && (
        <p className="feedback-muted">
          Tarayıcı hash hesaplayamadı; kopya kontrolü yükleme anında sunucuda yapılacak.
        </p>
      )}

      {(check || result) && (
        <div
          className={`verdict ${blocked ? 'verdict-blocked' : warned ? 'verdict-warn' : 'verdict-ok'}`}
          role="status"
        >
          <p>
            <strong>
              {blocked ? 'Bu doküman zaten var' : warned ? 'Benzer doküman bulundu' : 'Yeni doküman'}
            </strong>
          </p>
          <p>{result?.message ?? check?.message}</p>

          {matches.length > 0 && (
            <ul className="verdict-matches">
              {matches.map((match: DocumentDto) => (
                <li key={match.id}>
                  <span className="duplicate-name">{match.fileName}</span>
                  <span className="muted">
                    {match.department} · {match.uploadedBy} · {formatDate(match.createdAt)}
                  </span>
                  <button
                    type="button"
                    className="link"
                    onClick={() => onSearchExisting(match.fileName)}
                  >
                    aramada göster
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {error && <p className="feedback feedback-error">{error}</p>}

      <div className="upload-actions">
        <button type="button" disabled={!file || busy || blocked} onClick={() => submit(false)}>
          {busy ? 'Yükleniyor…' : 'Yükle'}
        </button>

        {/* Sert engelleme yerine bilinçli onay: nadir de olsa gerçekten aynı içeriğin
            ikinci kez kaydedilmesi gereken durumlar var (farklı departman arşivi vb.). */}
        {(blocked || warned) && (
          <button type="button" className="ghost" disabled={!file || busy} onClick={() => submit(true)}>
            Yine de yükle
          </button>
        )}

        {file && (
          <button type="button" className="ghost" onClick={reset}>
            Vazgeç
          </button>
        )}
      </div>

      {result?.verdict === 'created' && (
        <p className="feedback feedback-ok">{result.message}</p>
      )}
    </section>
  )
}
