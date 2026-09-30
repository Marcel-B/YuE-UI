// Mirrors the JSON contract of YueToLogic.Core (camelCase, enums as PascalCase strings) as the Logic page's
// endpoints hand it over; kept apart from ../types.ts, which mirrors YuE UI's own records.

export type Severity = 'Info' | 'Warning' | 'Error'

export interface Diagnostic {
  severity: Severity
  code: string
  message: string
  line: number | null
  column: number | null
}

export interface NoteEvent {
  startTicks: number
  durationTicks: number
  noteNumber: number
  velocity: number | null
}

export type TrackKind = 'Melody' | 'Chords' | 'Bass' | 'Drums' | 'GuideTones' | 'Doubling' | 'Harmony'

export interface VoiceTrack {
  id: string
  displayName: string
  notes: NoteEvent[]
  kind: TrackKind
}

export interface TimeSignatureChange {
  startTicks: number
  numerator: number
  denominator: number
}

export interface KeySignatureChange {
  startTicks: number
  key: string
  sharps: number
  isMinor: boolean
}

export interface SectionMarker {
  startTicks: number
  name: string
}

export interface ChordEvent {
  startTicks: number
  durationTicks: number
  text: string
  symbol: { rootPitchClass: number; quality: string; bassPitchClass: number | null } | null
}

export interface ScoreDocument {
  ticksPerQuarterNote: number
  title: string | null
  tempoBpm: number
  timeSignatures: TimeSignatureChange[]
  keySignatures: KeySignatureChange[]
  sections: SectionMarker[]
  voices: VoiceTrack[]
  chords: ChordEvent[]
  lengthTicks: number
  /** Silent lead-in before the music; the recording starts here. */
  countInTicks: number
  durationSeconds: number
}

export interface ConversionResult {
  success: boolean
  score: ScoreDocument | null
  /** Standard MIDI File, base64-encoded. */
  midi: string | null
  diagnostics: Diagnostic[]
}

export type BassPattern = 'Eighths' | 'Quarters' | 'RootFifth' | 'Octaves' | 'Offbeat' | 'Sustained' | 'Walking'

export type DrumPattern = 'FourOnTheFloor' | 'Backbeat' | 'HalfTime' | 'Disco' | 'SixteenthHats' | 'Shuffle'

export type ChordPattern = 'Block' | 'Eighths' | 'Sixteenths' | 'Offbeat' | 'ArpeggioUp' | 'ArpeggioUpDown'

export type ChordInversion = 'RootPosition' | 'Closest' | 'First' | 'Second'

export type SwingUnit = 'Eighths' | 'Sixteenths'

export type HarmonyPart = 'ThirdAbove' | 'ThirdBelow' | 'SixthBelow' | 'Alto' | 'Tenor' | 'Bass' | 'Drone'

/** Every backing part in the order the form lists them. */
export const HARMONY_PARTS: HarmonyPart[] = ['ThirdAbove', 'ThirdBelow', 'SixthBelow', 'Alto', 'Tenor', 'Bass', 'Drone']

/** The track name the converter gives a backing part (`HarmonyGenerator.TrackId`). */
export function harmonyTrack(part: HarmonyPart): string {
  const names: Record<HarmonyPart, string> = {
    ThirdAbove: 'Harmony 3rd up',
    ThirdBelow: 'Harmony 3rd down',
    SixthBelow: 'Harmony 6th down',
    Alto: 'Harmony Alto',
    Tenor: 'Harmony Tenor',
    Bass: 'Harmony Bass',
    Drone: 'Harmony Drone',
  }
  return names[part]
}

export interface ConversionOptions {
  ticksPerQuarterNote: number
  includeChordTrack: boolean
  arrangement: {
    defaultOctaveShift: number
    octaveShifts: Record<string, number>
    bass: { pattern: BassPattern; octaveShift: number } | null
    drums: { pattern: DrumPattern; crashOnSections: boolean; separateTracks: boolean; notes: DrumNotes | null } | null
    chords: { pattern: ChordPattern; inversion: ChordInversion; octaveShift: number } | null
    guideTones: { octaveShift: number } | null
    doubling: { voiceId: string; semitones: number } | null
    /** `key` null takes the score's key where the melody fits it, else the one the melody suggests. */
    harmony: { parts: HarmonyPart[]; voiceId: string; key: string | null; sections: string[] } | null
    groove: {
      swing: number
      swingUnit: SwingUnit
      includeDrums: boolean
      humanizeTimingMs: number
      humanizeVelocity: number
    } | null
    mono: { legato: boolean } | null
    countIn: { bars: number; click: boolean } | null
  }
  /** MIDI channel (1-16) per track name; a track without an entry gets the next free one. */
  midiChannels: Record<string, number>
  /** Program change (1-128) sent at the start of the track, per track name. */
  midiPrograms: Record<string, number>
}

/** The tracks a conversion can produce, in the order the MIDI file lists them. */
export const TRACK_NAMES = [
  'Vocal',
  'Ins',
  'Vocal 8vb',
  ...HARMONY_PARTS.map(harmonyTrack),
  'Chords',
  'Bass',
  'Drums',
  'Guide',
  'Kick',
  'Snare',
  'HiHat',
  'Crash',
] as const

/** The note each drum of the generated kit is played on; General MIDI unless a drum machine says otherwise. */
export interface DrumNotes {
  kick: number
  snare: number
  closedHiHat: number
  openHiHat: number
  crash: number
  clap: number
}

/** The General MIDI drum map, what the generator plays without a drum machine. */
export const GENERAL_MIDI_DRUMS: DrumNotes = {
  kick: 36,
  snare: 38,
  closedHiHat: 42,
  openHiHat: 46,
  crash: 49,
  clap: 39,
}

/** The drums of the kit in the order the interface lists them. */
export const DRUMS = [
  'kick',
  'snare',
  'closedHiHat',
  'openHiHat',
  'crash',
  'clap',
] as const satisfies readonly (keyof DrumNotes)[]

/**
 * A synthesizer plays a track on its port and channel; a drum machine plays several drums on one channel,
 * each on a note of its own, which the drum tracks that play it are generated on.
 */
export type InstrumentKind = 'Synth' | 'DrumMachine'

/** A hardware instrument as the server keeps it: a name for a MIDI port (as Web MIDI names it) and a channel, 1-16. */
export interface Instrument {
  id: number
  name: string
  port: string
  channel: number
  kind: InstrumentKind
  /** Only a drum machine has them. */
  drums: DrumNotes | null
}

export interface InstrumentInput {
  name: string
  port: string
  channel: number
  kind: InstrumentKind
  drums: DrumNotes | null
}

/** Track name → id of the instrument that plays it. */
export type Assignments = Record<string, number>

/** What the Logic export is told about a track's instrument. */
export interface LogicInstrument {
  name: string
  port: string
  channel: number
}
