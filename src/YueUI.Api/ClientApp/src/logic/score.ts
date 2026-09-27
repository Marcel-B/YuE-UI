import type { ChordEvent, ConversionResult, ScoreDocument, VoiceTrack } from './types'

/** 1-based bar at a tick position, honouring meter changes (same rule as ScoreDocument.GetBarPosition). */
export function barAt(score: ScoreDocument, ticks: number): number {
  let barsBefore = 0
  const signatures = score.timeSignatures
  for (let i = 0; i < signatures.length; i++) {
    const signature = signatures[i]!
    const measureTicks = ((4 * score.ticksPerQuarterNote) / signature.denominator) * signature.numerator
    const segmentEnd = signatures[i + 1]?.startTicks ?? Number.POSITIVE_INFINITY
    if (ticks < segmentEnd) {
      return barsBefore + Math.floor(Math.max(0, ticks - signature.startTicks) / measureTicks) + 1
    }
    barsBefore += Math.ceil((segmentEnd - signature.startTicks) / measureTicks)
  }
  return 1
}

export function barCount(score: ScoreDocument): number {
  return barAt(score, score.lengthTicks) - 1
}

/** "score.abc" → "score"; the name the downloads are offered under. */
export function baseName(fileName: string): string {
  const dot = fileName.lastIndexOf('.')
  return dot > 0 ? fileName.slice(0, dot) : fileName
}

export function midiBlob(result: ConversionResult): Blob | null {
  if (!result.midi) {
    return null
  }
  const binary = atob(result.midi)
  const bytes = new Uint8Array(binary.length)
  for (let i = 0; i < binary.length; i++) {
    bytes[i] = binary.charCodeAt(i)
  }
  return new Blob([bytes], { type: 'audio/midi' })
}

/** The same dump the CLI writes with --dump-json: everything except the MIDI bytes. */
export function jsonBlob(result: ConversionResult): Blob {
  return new Blob([JSON.stringify({ ...result, midi: null }, null, 2)], { type: 'application/json' })
}

export function download(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const link = document.createElement('a')
  link.href = url
  link.download = fileName
  document.body.append(link)
  link.click()
  link.remove()
  setTimeout(() => URL.revokeObjectURL(url), 0)
}

/**
 * The chord vocabulary of the YuE2 dialect as semitones above the root. Mirrors ChordVoicing.GetIntervals in
 * the core library; it is repeated here so the preview can show the chord track the renderer builds from the
 * bare symbols when no chord pattern was chosen. The vocabulary is closed, so the two cannot drift apart
 * without the parser changing as well.
 */
const CHORD_INTERVALS: Record<string, number[]> = {
  Major: [0, 4, 7],
  Minor: [0, 3, 7],
  Diminished: [0, 3, 6],
  Augmented: [0, 4, 8],
  Dominant7: [0, 4, 7, 10],
  Major7: [0, 4, 7, 11],
  Minor7: [0, 3, 7, 10],
  Diminished7: [0, 3, 6, 9],
  HalfDiminished7: [0, 3, 6, 10],
  Suspended4: [0, 5, 7],
  Suspended2: [0, 2, 7],
  Major6: [0, 4, 7, 9],
  Minor6: [0, 3, 7, 9],
  Dominant7Suspended4: [0, 5, 7, 10],
  MinorMajor7: [0, 3, 7, 11],
}

/** MIDI note of C3, the octave the chord root is placed in (ChordVoicing.DefaultRootOctaveBase). */
const ROOT_OCTAVE_BASE = 48

function blockChord(chord: ChordEvent): number[] {
  if (!chord.symbol) {
    return []
  }
  const intervals = CHORD_INTERVALS[chord.symbol.quality] ?? []
  const notes = intervals.map((interval) => ROOT_OCTAVE_BASE + chord.symbol!.rootPitchClass + interval)
  return chord.symbol.bassPitchClass === null ? notes : [ROOT_OCTAVE_BASE - 12 + chord.symbol.bassPitchClass, ...notes]
}

/**
 * The voices as they will sound. Without a chord pattern the arranger leaves the chord track to the renderer,
 * which plays one block chord per symbol; the preview would otherwise show and play nothing for the chords.
 */
export function playableVoices(score: ScoreDocument, includeChords: boolean): VoiceTrack[] {
  const voices = score.voices.filter((voice) => includeChords || voice.kind !== 'Chords')
  if (!includeChords || score.chords.length === 0 || voices.some((voice) => voice.kind === 'Chords')) {
    return voices
  }

  const notes = score.chords.flatMap((chord) =>
    blockChord(chord).map((noteNumber) => ({
      startTicks: chord.startTicks,
      durationTicks: chord.durationTicks,
      noteNumber,
      velocity: 72,
    })),
  )
  const melodies = voices.filter((voice) => voice.kind === 'Melody' || voice.kind === 'Doubling').length
  const chordTrack: VoiceTrack = { id: 'Chords', displayName: 'Chords', notes, kind: 'Chords' }
  return [...voices.slice(0, melodies), chordTrack, ...voices.slice(melodies)]
}
