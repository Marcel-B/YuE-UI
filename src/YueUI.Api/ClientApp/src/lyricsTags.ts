/**
 * Section tags for the lyrics. YuE2 has no tag list and passes the lyrics to the model as plain text, so a tag counts
 * by what the model learned: the sections its own scores name (`% intro`, `% verse`, `% pre-chorus`, `% chorus`,
 * `% interlude`, `% bridge`, `% outro` in the planned score.abc), plus the four YuE v1 documented. The others are
 * common in lyric sheets, but neither YuE documents them: they reach the model as words, and whether a [Drop] drops
 * is up to it.
 */
export const knownSectionTags = ['[Intro]', '[Verse]', '[Pre-Chorus]', '[Chorus]', '[Bridge]', '[Interlude]', '[Outro]']

export const otherSectionTags = ['[Hook]', '[Solo]', '[Break]', '[Build-Up]', '[Drop]']

export interface Insertion {
  text: string
  /** Where the caret belongs afterwards: the start of the line below the tag. */
  caret: number
}

const isTagLine = (line: string): boolean => /^\s*\[[^\]]+\]\s*$/.test(line)

/**
 * Puts the tag on a line of its own at the caret: before the caret's line when it sits at a line's start, else after
 * that line, since a tag must not split a sung line. A tag after a sung line gets an empty line before it, as sections
 * are written; tags in a row (an instrumental's structure) stay together.
 */
export function insertSectionTag(lyrics: string, tag: string, caret: number | null): Insertion {
  let at = Math.min(Math.max(caret ?? lyrics.length, 0), lyrics.length)
  if (at > 0 && lyrics[at - 1] !== '\n') {
    const end = lyrics.indexOf('\n', at)
    at = end === -1 ? lyrics.length : end
  }
  let before = lyrics.slice(0, at)
  const after = lyrics.slice(at)
  if (before.trim() === '') {
    before = ''
  } else {
    before = before.replace(/\n*$/, '')
    const previous = before.slice(before.lastIndexOf('\n') + 1)
    before += isTagLine(previous) ? '\n' : '\n\n'
  }
  const inserted = `${tag}\n`
  // Text after the caret's line already starts on a line of its own; the leading newline of `after` stays its own.
  const rest = after.startsWith('\n') ? after.slice(1) : after
  return { text: before + inserted + rest, caret: before.length + inserted.length }
}
