/**
 * MIDI note names as Logic shows them, middle C (60) being C3: what the user reads off Logic's piano roll and
 * off a drum machine's manual when looking up which note a drum sits on. Mirrors NoteNames in the library.
 */

const NAMES = ['C', 'C#', 'D', 'D#', 'E', 'F', 'F#', 'G', 'G#', 'A', 'A#', 'B']
const LOWEST_OCTAVE = -2
const NATURAL: Record<string, number> = { C: 0, D: 2, E: 4, F: 5, G: 7, A: 9, B: 11 }

/** "C1" for 36, "C3" for 60, "C-2" for 0. */
export function noteName(note: number): string {
  return `${NAMES[note % 12]}${LOWEST_OCTAVE + Math.floor(note / 12)}`
}

/** A note from a number ("36") or a name ("C1", "F#1", "Bb0", "c-2"); null when it is neither, or off the keyboard. */
export function parseNote(text: string): number | null {
  const trimmed = text.trim()
  if (/^\d+$/.test(trimmed)) {
    const number = Number(trimmed)
    return number <= 127 ? number : null
  }
  const match = /^([A-Ga-g])([#b]*)(-?\d+)$/.exec(trimmed)
  if (!match) {
    return null
  }
  const semitone =
    NATURAL[match[1]!.toUpperCase()]! +
    [...match[2]!].reduce((sum, accidental) => sum + (accidental === '#' ? 1 : -1), 0)
  const note = (Number(match[3]) - LOWEST_OCTAVE) * 12 + semitone
  return note >= 0 && note <= 127 ? note : null
}
