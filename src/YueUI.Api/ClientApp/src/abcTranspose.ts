/**
 * Transposes a score in YuE2's ABC dialect by semitones: notes of every voice, chord symbols and key signatures.
 *
 * The model sings the "Vocal" voice of a supplied score at the written pitch, so this is how a song gets lower or
 * higher before it is generated. Notes are respelled in the new key: a transposed score reads like one YuE2 wrote.
 * It also turns a score from major into minor on the same root and back (`changeMode`), since YuE2 reads a minor
 * `K:` as well, but only the notes make the song sound minor.
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

function pitchClassOf(letter: string, accidental: string): number {
  return mod12(naturals[letters.indexOf(letter)]! + (accidental === '#' ? 1 : accidental === 'b' ? -1 : 0))
}

// YuE2's chord vocabulary as intervals above the root, to find a chord's new quality after a change of mode.
const chordIntervals: Record<string, number[]> = {
  '': [0, 4, 7],
  m: [0, 3, 7],
  dim: [0, 3, 6],
  aug: [0, 4, 8],
  '7': [0, 4, 7, 10],
  maj7: [0, 4, 7, 11],
  m7: [0, 3, 7, 10],
  dim7: [0, 3, 6, 9],
  m7b5: [0, 3, 6, 10],
  sus4: [0, 5, 7],
  sus2: [0, 2, 7],
  '6': [0, 4, 7, 9],
  m6: [0, 3, 7, 9],
  '7sus4': [0, 5, 7, 10],
  'm(maj7)': [0, 3, 7, 11],
}

/**
 * "Am7", "F#", "C/E", "Bbmaj7/D": root and bass move by `move`. With `tones`, the chord's notes move too and the
 * quality becomes the one they form (C in C major is Cm in C minor); a quality YuE2 does not know stays. Anything
 * else is left alone.
 */
function rewriteChord(symbol: string, move: (pitchClass: number) => number, key: Key, tones: boolean): string {
  const match = /^([A-G])([#b]?)([^/]*)(?:\/([A-G])([#b]?))?$/.exec(symbol)
  if (!match) {
    return symbol
  }
  const root = pitchClassOf(match[1]!, match[2]!)
  const newRoot = mod12(move(root))
  let quality = match[3]!
  const intervals = chordIntervals[quality]
  if (tones && intervals) {
    const moved = intervals.map((interval) => mod12(move(root + interval) - newRoot)).sort((a, b) => a - b)
    const found = Object.entries(chordIntervals).find(
      ([, candidate]) => candidate.length === moved.length && candidate.every((interval, i) => interval === moved[i]),
    )
    quality = found ? found[0] : quality
  }
  const bass = match[4] ? '/' + spellName(mod12(move(pitchClassOf(match[4], match[5]!))), key) : ''
  return spellName(newRoot, key) + quality + bass
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

/** How a rewrite changes keys and notes; `pitch` gets the key the note was written in. */
interface Rewrite {
  key(key: Key): Key
  pitch(pitch: number, from: Key): number
  /** Whether chords change their quality with their notes (a change of mode) or keep it (a transposition). */
  chordTones: boolean
}

function transposition(semitones: number): Rewrite {
  return { key: (key) => transposeKey(key, semitones), pitch: (pitch) => pitch + semitones, chordTones: false }
}

function sameRoot(key: Key, minor: boolean): Key {
  const name = (minor ? minorKeys : majorKeys)[keyRoot(key)]!
  return { name, minor, sharps: signatures[name]! }
}

/**
 * Major to natural minor on the same root and back: third, sixth and seventh go down a semitone (or up). Natural
 * rather than harmonic minor, since it keeps the chords within YuE2's vocabulary (G7 in C becomes Gm7 in Cm, not
 * an augmented chord on Eb); a raised seventh already in a minor score stays where it is.
 */
const modeChange: Rewrite = {
  key: (key) => sameRoot(key, !key.minor),
  pitch: (pitch, from) => {
    const degree = mod12(pitch - keyRoot(from))
    if (from.minor) {
      return degree === 3 || degree === 8 || degree === 10 ? pitch + 1 : pitch
    }
    return degree === 4 || degree === 9 || degree === 11 ? pitch - 1 : pitch
  },
  chordTones: true,
}

/** Transposes a whole score. Header fields, comments and anything that is not a note or chord stay as they are. */
export function transposeAbc(abc: string, semitones: number): Transposition {
  return rewriteAbc(abc, transposition(semitones))
}

/** Turns a major score into minor on the same root (D into Dm) or a minor one into major, at every key change. */
export function changeMode(abc: string): Transposition {
  return rewriteAbc(abc, modeChange)
}

function rewriteAbc(abc: string, rewrite: Rewrite): Transposition {
  let oldKey = cMajor
  let newKey = rewrite.key(cMajor)
  let firstKey: string | null = null
  const lines = abc.split('\n').map((line) => {
    const field = /^(\s*K:\s*)(.*)$/.exec(line)
    if (field) {
      const key = parseKey(field[2]!)
      if (!key) {
        return line
      }
      oldKey = key
      newKey = rewrite.key(key)
      firstKey ??= newKey.name
      return field[1] + field[2]!.replace(/^\S+/, newKey.name)
    }
    // Other fields (X:, M:, V:, w:, …) and comments hold no notes.
    if (/^\s*([A-Za-z]:|%)/.test(line)) {
      return line
    }
    const rewritten = rewriteMusic(line, rewrite, oldKey, newKey)
    oldKey = rewritten.oldKey
    newKey = rewritten.newKey
    return rewritten.text
  })
  return { abc: lines.join('\n'), key: firstKey ?? newKey.name }
}

function rewriteMusic(line: string, rewrite: Rewrite, oldKey: Key, newKey: Key) {
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
      const from = before.key
      const move = (pitchClass: number) => rewrite.pitch(pitchClass, from)
      out += '"' + rewriteChord(line.slice(i + 1, end), move, after.key, rewrite.chordTones) + '"'
      i = end + 1
      continue
    }
    if (c === '[' && /^\[[A-Za-z]:/.test(line.slice(i))) {
      const end = line.indexOf(']', i)
      const inline = end < 0 ? line.slice(i) : line.slice(i, end + 1)
      const key = inline.startsWith('[K:') ? parseKey(inline.slice(3, end < 0 ? undefined : -1)) : null
      if (key) {
        const moved = rewrite.key(key)
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
      pitch = rewrite.pitch(pitch + before.resolve(letter, explicit), before.key)
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

/** The key the score starts in, spelled as in `keyChoices` (Gb as F#), so the picker finds it. */
export function abcKey(abc: string): string | null {
  const field = /^\s*K:\s*(.*)$/m.exec(abc)
  const key = field ? parseKey(field[1]!) : null
  if (!key) {
    return null
  }
  return (key.minor ? minorKeys : majorKeys)[keyRoot(key)]!
}

/** Every major and minor key to pick a target from; the other mode changes the notes too (`changeMode`). */
export function keyChoices(): string[] {
  return [...majorKeys, ...minorKeys]
}

/** "F#m" as "F♯" and minor, for a label in the reader's language. */
export function keyParts(name: string): { root: string; minor: boolean } {
  const minor = name.endsWith('m')
  return { root: (minor ? name.slice(0, -1) : name).replace('#', '♯').replace(/(?<=.)b/, '♭'), minor }
}

/**
 * Rewrites the score into the named key (one of `keyChoices`): into the other mode on the same root first if the
 * target has it, then transposed by the shorter way, at most six semitones down or five up, so the voice stays near
 * where it was; − and + go on from there.
 */
export function transposeToKey(abc: string, target: string): Transposition {
  const field = /^\s*K:\s*(.*)$/m.exec(abc)
  let from = (field ? parseKey(field[1]!) : null) ?? cMajor
  const to = parseKey(target)
  if (!to) {
    return { abc, key: from.name }
  }
  if (to.minor !== from.minor) {
    abc = changeMode(abc).abc
    from = sameRoot(from, to.minor)
  }
  const up = mod12(keyRoot(to) - keyRoot(from))
  return transposeAbc(abc, up > 5 ? up - 12 : up)
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
