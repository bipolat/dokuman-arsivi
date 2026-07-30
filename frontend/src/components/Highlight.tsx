import { highlightParts } from '../utils'

export function Highlight({ text, query }: { text: string; query: string }) {
  const parts = highlightParts(text, query)
  return (
    <>
      {parts.map((part, index) =>
        part.hit ? <mark key={index}>{part.text}</mark> : <span key={index}>{part.text}</span>,
      )}
    </>
  )
}
