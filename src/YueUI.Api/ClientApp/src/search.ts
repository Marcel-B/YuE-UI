import type { RunInfo } from './types'

/**
 * Where the library search looks. Title and style are short and describe the song, lyrics are long and share most
 * words with every other song ("love", "night"); searched together, the lyrics would bury the few hits that matter.
 */
export type SearchScope = 'titleStyle' | 'lyrics'

/** A part of a line, marked where it matched, so the template can highlight it without `v-html`. */
export interface Segment {
  text: string
  hit: boolean
}

/** Folds case and accents, so "traume" finds "Träume" and "cafe" finds "Café". */
function fold(text: string): string {
  return text.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase()
}

/**
 * Words separated by spaces must all occur, in any order; a quoted part must occur as written, which narrows a
 * search in the lyrics to a remembered line.
 */
export function parseQuery(query: string): string[] {
  const terms: string[] = []
  for (const match of query.matchAll(/"([^"]*)"?|(\S+)/g)) {
    const term = fold((match[1] ?? match[2] ?? '').trim().replace(/\s+/g, ' '))
    if (term) {
      terms.push(term)
    }
  }
  return terms
}

function haystack(run: RunInfo, scope: SearchScope): string {
  // Line breaks become spaces, so a quoted phrase also finds a line the lyrics wrap.
  const text = scope === 'lyrics' ? run.lyrics : [run.title, run.originalTitle, run.style].join('\n')
  return fold(text).replace(/\s+/g, ' ')
}

export function matchesRun(run: RunInfo, terms: string[], scope: SearchScope): boolean {
  if (terms.length === 0) {
    return true
  }
  const text = haystack(run, scope)
  return terms.every((term) => text.includes(term))
}

/**
 * Splits a line into matched and unmatched parts. Folding can change a text's length (a decomposed "ä" loses its
 * mark), so the positions found in the folded line are mapped back to the original characters.
 */
export function highlight(line: string, terms: string[]): Segment[] {
  let folded = ''
  const origin: number[] = []
  let offset = 0
  for (const char of line) {
    const part = fold(char)
    folded += part
    for (let i = 0; i < part.length; i++) {
      origin.push(offset)
    }
    offset += char.length
  }
  origin.push(line.length)

  const hit = new Array<boolean>(line.length).fill(false)
  for (const term of terms) {
    for (let at = folded.indexOf(term); at >= 0; at = folded.indexOf(term, at + 1)) {
      for (let i = origin[at]!; i < origin[at + term.length]!; i++) {
        hit[i] = true
      }
    }
  }

  const segments: Segment[] = []
  for (let i = 0; i < line.length; i++) {
    const last = segments[segments.length - 1]
    if (last && last.hit === hit[i]) {
      last.text += line[i]
    } else {
      segments.push({ text: line[i]!, hit: hit[i]! })
    }
  }
  return segments
}

/** The lyric lines that contain a search term, so a hit shows where it is without opening the whole text. */
export function matchingLines(lyrics: string, terms: string[], limit = 3): Segment[][] {
  const lines: Segment[][] = []
  for (const line of lyrics.split('\n')) {
    const trimmed = line.trim()
    const text = fold(trimmed)
    // Section tags like "[verse]" say nothing about the song's words.
    if (!trimmed || /^\[.*\]$/.test(trimmed) || !terms.some((term) => text.includes(term))) {
      continue
    }
    lines.push(highlight(trimmed, terms))
    if (lines.length === limit) {
      break
    }
  }
  return lines
}
