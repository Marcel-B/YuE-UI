/**
 * Transposes a score in YuE2's ABC dialect by semitones: notes of every voice, chord symbols and key signatures.
 *
 * The model sings the "Vocal" voice of a supplied score at the written pitch, so this is how a song gets lower or
 * higher before it is generated. Notes are respelled in the new key: a transposed score reads like one YuE2 wrote.
 */

const letters = 'CDEFGAB'
const naturals = [0, 2, 4, 5, 7, 9, 11]
const sharpOrder = 'FCGDAEB'
const flatOrder = 'BEADGCF'

// Index = pitch class of the root. One spelling per class, at most six accidentals (F# over Gb, Ebm over D#m).
const majorKeys = ['C', 'Db', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab', 'A', 'Bb', 'B']
const minorKeys = ['Cm', 'C#m', 'Dm', 'Ebm', 'Em', 'Fm', 'F#m', 'Gm', 'G#m', 'Am', 'Bbm', 'Bm']
const signatures: Record<string, number> = {
  Cb: -7,
  Gb: -6,
  Db: -5,
  Ab: -4,
  Eb: -3,
  Bb: -2,
  F: -1,
  C: 0,
  G: 1,
  D: 2,
  A: 3,
  E: 4,
  B: 5,
  'F#': 6,
  'C#': 7,
  Abm: -7,
  Ebm: -6,
  Bbm: -5,
  Fm: -4,
  Cm: -3,
  Gm: -2,
  Dm: -1,
  Am: 0,
  Em: 1,
  Bm: 2,
  'F#m': 3,
  'C#m': 4,
  'G#m': 5,
  'D#m': 6,
  'A#m': 7,
}

interface Key {
  name: string
  minor: boolean
  sharps: number
}

const cMajor: Key = { name: 'C', minor: false, sharps: 0 }

function mod12(value: number): number {
  return ((value % 12) + 12) % 12
}

/** Reads the key of a `K:` field; clef and other `name=value` attributes are ignored. */
function parseKey(text: string): Key | null {
  const compact = text
    .split(/\s+/)
    .filter((part) => part !== '' && !part.includes('='))
    .join('')
  const match = /^([A-Ga-g])([#b]?)(.*)$/.exec(compact)
  if (!match) {
    return null
  }
  const mode = match[3]!.toLowerCase()
  const minor = ['m', 'min', 'minor', 'aeo', 'aeolian'].includes(mode)
  if (!minor && !['', 'maj', 'major', 'ion', 'ionian'].includes(mode)) {
    return null
  }
  const name = match[1]!.toUpperCase() + match[2] + (minor ? 'm' : '')
  const sharps = signatures[name]
  return sharps === undefined ? null : { name, minor, sharps }
}

function keyAlteration(key: Key, letter: string): number {
  if (key.sharps > 0) {
    return sharpOrder.indexOf(letter) < key.sharps ? 1 : 0
  }
  if (key.sharps < 0) {
    return flatOrder.indexOf(letter) < -key.sharps ? -1 : 0
  }
  return 0
}

function keyRoot(key: Key): number {
  const letter = key.name[0]!
  return mod12(naturals[letters.indexOf(letter)]! + (key.name[1] === '#' ? 1 : key.name[1] === 'b' ? -1 : 0))
}

function transposeKey(key: Key, semitones: number): Key {
  const root = mod12(keyRoot(key) + semitones)
  const name = key.minor ? minorKeys[root]! : majorKeys[root]!
  return { name, minor: key.minor, sharps: signatures[name]! }
}

/** Letter and alteration for a pitch class: the key's own note if there is one, else sharps or flats as the key has. */
function spell(pitchClass: number, key: Key): { letter: string; alteration: number } {
  for (let i = 0; i < 7; i++) {
    const alteration = keyAlteration(key, letters[i]!)
    if (mod12(naturals[i]! + alteration) === pitchClass) {
      return { letter: letters[i]!, alteration }
    }
  }
  // A note outside the key: natural if it is one, else with the accidentals the key uses (without a signature
  // C#, Eb, F#, G#, Bb, as they are usually written).
  const direction = key.sharps < 0 || (key.sharps === 0 && (pitchClass === 3 || pitchClass === 10)) ? -1 : 1
  for (const alteration of [0, direction, -direction]) {
    const i = naturals.findIndex((natural) => mod12(natural + alteration) === pitchClass)
    if (i >= 0) {
      return { letter: letters[i]!, alteration }
    }
  }
  throw new Error(`No spelling for pitch class ${pitchClass}`)
}

function spellName(pitchClass: number, key: Key): string {
  const { letter, alteration } = spell(pitchClass, key)
  return letter + (alteration > 0 ? '#'.repeat(alteration) : 'b'.repeat(-alteration))
}

/** "Am7", "F#", "C/E", "Bbmaj7/D": root and bass move, the quality stays. Anything else is left alone. */
function transposeChord(symbol: string, semitones: number, key: Key): string {
  const match = /^([A-G])([#b]?)([^/]*)(?:\/([A-G])([#b]?))?$/.exec(symbol)
  if (!match) {
    return symbol
  }
  const shift = (letter: string, accidental: string) =>
    spellName(
      mod12(naturals[letters.indexOf(letter)]! + (accidental === '#' ? 1 : accidental === 'b' ? -1 : 0) + semitones),
      key,
    )
  const bass = match[4] ? '/' + shift(match[4], match[5]!) : ''
  return shift(match[1]!, match[2]!) + match[3] + bass
}

function accidentalText(alteration: number): string {
  return ['__', '_', '=', '^', '^^'][alteration + 2]!
}

function noteText(pitch: number, letter: string, alteration: number): string {
  // The octave follows from the natural letter: B# and Cb sit on the other side of the octave boundary.
  const natural = pitch - alteration
  const octave = Math.floor((natural - 60) / 12)
  if (octave >= 1) {
    return letter.toLowerCase() + "'".repeat(octave - 1)
  }
  return letter + ','.repeat(-octave)
}

/**
 * Accidentals as YuE2 reads them: one applies to its note letter in every octave until the next bar line or key
 * change (after `^F`, `f` is sharp too). Both the old and the new spelling follow that rule.
 */
class Bar {
  private readonly byLetter = new Map<string, number>()
  key: Key

  constructor(key: Key) {
    this.key = key
  }

  resolve(letter: string, explicit: number | null): number {
    if (explicit !== null) {
      this.byLetter.set(letter, explicit)
      return explicit
    }
    return this.byLetter.get(letter) ?? keyAlteration(this.key, letter)
  }

  /** The accidental to write for a note, or '' when the key and the bar already give it. */
  write(letter: string, alteration: number): string {
    if (this.resolve(letter, null) === alteration) {
      return ''
    }
    this.byLetter.set(letter, alteration)
    return accidentalText(alteration)
  }

  reset(key = this.key): void {
    this.byLetter.clear()
    this.key = key
  }
}

export interface Transposition {
  abc: string
  /** The first key of the result, e.g. "A" or "F#m". */
  key: string
}

/** Transposes a whole score. Header fields, comments and anything that is not a note or chord stay as they are. */
export function transposeAbc(abc: string, semitones: number): Transposition {
  let oldKey = cMajor
  let newKey = transposeKey(cMajor, semitones)
  let firstKey: string | null = null
  const lines = abc.split('\n').map((line) => {
    const field = /^(\s*K:\s*)(.*)$/.exec(line)
    if (field) {
      const key = parseKey(field[2]!)
      if (!key) {
        return line
      }
      oldKey = key
      newKey = transposeKey(key, semitones)
      firstKey ??= newKey.name
      return field[1] + field[2]!.replace(/^\S+/, newKey.name)
    }
    // Other fields (X:, M:, V:, w:, …) and comments hold no notes.
    if (/^\s*([A-Za-z]:|%)/.test(line)) {
      return line
    }
    const transposed = transposeMusic(line, semitones, oldKey, newKey)
    oldKey = transposed.oldKey
    newKey = transposed.newKey
    return transposed.text
  })
  return { abc: lines.join('\n'), key: firstKey ?? newKey.name }
}

function transposeMusic(line: string, semitones: number, oldKey: Key, newKey: Key) {
  const before = new Bar(oldKey)
  const after = new Bar(newKey)
  let out = ''
  let i = 0
  while (i < line.length) {
    const c = line[i]!
    if (c === '%') {
      out += line.slice(i)
      break
    }
    if (c === '"') {
      const end = line.indexOf('"', i + 1)
      if (end < 0) {
        out += line.slice(i)
        break
      }
      out += '"' + transposeChord(line.slice(i + 1, end), semitones, after.key) + '"'
      i = end + 1
      continue
    }
    if (c === '[' && /^\[[A-Za-z]:/.test(line.slice(i))) {
      const end = line.indexOf(']', i)
      const inline = end < 0 ? line.slice(i) : line.slice(i, end + 1)
      const key = inline.startsWith('[K:') ? parseKey(inline.slice(3, end < 0 ? undefined : -1)) : null
      if (key) {
        const moved = transposeKey(key, semitones)
        before.reset(key)
        after.reset(moved)
        out += `[K:${moved.name}]`
      } else {
        out += inline
      }
      i += inline.length
      continue
    }
    if (c === '!' || c === '+') {
      const end = line.indexOf(c, i + 1)
      const decoration = end < 0 ? line.slice(i) : line.slice(i, end + 1)
      out += decoration
      i += decoration.length
      continue
    }
    if (c === '|') {
      before.reset()
      after.reset()
      out += c
      i++
      continue
    }
    const note = /^(\^\^|__|\^|_|=)?([A-Ga-g])([',]*)/.exec(line.slice(i))
    if (note) {
      const letter = note[2]!.toUpperCase()
      const explicit = note[1] === undefined ? null : ({ '^^': 2, __: -2, '^': 1, _: -1, '=': 0 } as const)[note[1]]!
      let pitch = 60 + naturals[letters.indexOf(letter)]! + (note[2] === letter ? 0 : 12)
      for (const mark of note[3]!) {
        pitch += mark === "'" ? 12 : -12
      }
      pitch += before.resolve(letter, explicit) + semitones
      const spelled = spell(mod12(pitch), after.key)
      out += after.write(spelled.letter, spelled.alteration) + noteText(pitch, spelled.letter, spelled.alteration)
      i += note[0].length
      continue
    }
    out += c
    i++
  }
  return { text: out, oldKey: before.key, newKey: after.key }
}

/** The key the score starts in, for the label beside the buttons. */
export function abcKey(abc: string): string | null {
  const field = /^\s*K:\s*(.*)$/m.exec(abc)
  return field ? (parseKey(field[1]!)?.name ?? null) : null
}

const noteNames = ['C', 'C♯', 'D', 'E♭', 'E', 'F', 'F♯', 'G', 'A♭', 'A', 'B♭', 'B']

/** Lowest and highest note of the "Vocal" voice in scientific pitch (C4 = middle C), or null without one. */
export function vocalRange(abc: string): { low: string; high: string } | null {
  const pitches = vocalPitches(abc)
  if (pitches.length === 0) {
    return null
  }
  const name = (pitch: number) => noteNames[mod12(pitch)]! + (Math.floor(pitch / 12) - 1)
  return { low: name(Math.min(...pitches)), high: name(Math.max(...pitches)) }
}

/** MIDI pitches of the "Vocal" voice in order. */
export function vocalPitches(abc: string): number[] {
  let voice = ''
  let key = cMajor
  const pitches: number[] = []
  for (const line of abc.split('\n')) {
    const voiceField = /^\s*V:\s*(\S+)/.exec(line)
    if (voiceField) {
      voice = voiceField[1]!
      continue
    }
    const keyField = /^\s*K:\s*(.*)$/.exec(line)
    if (keyField) {
      key = parseKey(keyField[1]!) ?? key
      continue
    }
    if (/^\s*([A-Za-z]:|%)/.test(line) || voice !== 'Vocal') {
      continue
    }
    const bar = new Bar(key)
    const music = line.replace(/"[^"]*"|![^!]*!|\+[^+]*\+|%.*$/g, '')
    for (const token of music.matchAll(/\[K:([^\]]*)\]|\||(\^\^|__|\^|_|=)?([A-Ga-g])([',]*)/g)) {
      if (token[1] !== undefined) {
        key = parseKey(token[1]) ?? key
        bar.reset(key)
      } else if (token[0] === '|') {
        bar.reset()
      } else {
        const letter = token[3]!.toUpperCase()
        const explicit =
          token[2] === undefined ? null : ({ '^^': 2, __: -2, '^': 1, _: -1, '=': 0 } as const)[token[2]]!
        let pitch = 60 + naturals[letters.indexOf(letter)]! + (token[3] === letter ? 0 : 12)
        for (const mark of token[4]!) {
          pitch += mark === "'" ? 12 : -12
        }
        pitches.push(pitch + bar.resolve(letter, explicit))
      }
    }
  }
  return pitches
}
