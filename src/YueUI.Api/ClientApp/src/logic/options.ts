import type { Routing } from './midiPlayer'
import type { BassPattern, ChordInversion, ChordPattern, ConversionOptions, DrumPattern, SwingUnit } from './types'

/** What `--humanize 100` means in the CLI, so both hosts scatter the notes by the same amount. */
const maxHumanizeTimingMs = 25
const maxHumanizeVelocity = 24

/** The parameter form as the UI edits it; converted to the API's ConversionOptions on submit. */
export interface FormState {
  includeChords: boolean
  octave: number
  /** null = same as `octave`. */
  vocalOctave: number | null
  insOctave: number | null
  bass: 'off' | BassPattern
  bassOctave: number
  drums: 'off' | DrumPattern
  crash: boolean
  /** One track per drum instead of one drum track. */
  splitDrums: boolean
  /** 'as-written' plays one sustained block chord per symbol, as the score notates it. */
  chordPattern: 'as-written' | ChordPattern
  chordInversion: ChordInversion
  chordOctave: number
  guideTones: boolean
  guideOctave: number
  doubleVocal: boolean
  doubleOctave: number
  /** Swing in percent: 0 straight, 100 a full triplet feel. */
  swing: number
  swingUnit: SwingUnit
  straightDrums: boolean
  /** Humanization in percent of the maxima above. */
  humanize: number
  mono: boolean
  legato: boolean
  /** Silent bars in front of the song; 0 starts at bar 1. */
  countIn: number
  countInClick: boolean
  /** One region per song section in the Logic project; used by the Logic export only. */
  splitSections: boolean
  /** MIDI channel per track, 0 = the next free one. Keyed by track name. */
  channels: Record<string, number>
  /** Program change per track, 0 = none. Keyed by track name. */
  programs: Record<string, number>
  ppq: number
}

export const defaultFormState = (): FormState => ({
  includeChords: true,
  octave: 0,
  vocalOctave: null,
  insOctave: null,
  bass: 'off',
  bassOctave: 0,
  drums: 'off',
  crash: true,
  splitDrums: false,
  chordPattern: 'as-written',
  chordInversion: 'RootPosition',
  chordOctave: 0,
  guideTones: false,
  guideOctave: 0,
  doubleVocal: false,
  doubleOctave: -1,
  swing: 0,
  swingUnit: 'Eighths',
  straightDrums: false,
  humanize: 0,
  mono: false,
  legato: false,
  countIn: 0,
  countInClick: true,
  splitSections: false,
  channels: {},
  programs: {},
  ppq: 480,
})

export function toConversionOptions(form: FormState): ConversionOptions {
  const octaveShifts: Record<string, number> = {}
  if (form.vocalOctave !== null) {
    octaveShifts.Vocal = form.vocalOctave
  }
  if (form.insOctave !== null) {
    octaveShifts.Ins = form.insOctave
  }

  // A voicing or a register alone is reason enough for a chord track; it then plays the chords as written.
  const chordsAsWritten = form.chordPattern === 'as-written'
  const plainChords = chordsAsWritten && form.chordInversion === 'RootPosition' && form.chordOctave === 0

  return {
    ticksPerQuarterNote: form.ppq,
    includeChordTrack: form.includeChords,
    arrangement: {
      defaultOctaveShift: form.octave,
      octaveShifts,
      bass: form.bass === 'off' ? null : { pattern: form.bass, octaveShift: form.bassOctave },
      drums:
        form.drums === 'off'
          ? null
          : // The notes come from the drum machine a track is assigned to, which the app adds afterwards.
            { pattern: form.drums, crashOnSections: form.crash, separateTracks: form.splitDrums, notes: null },
      chords:
        !form.includeChords || plainChords
          ? null
          : {
              // Compared inline rather than through `chordsAsWritten`, so the type narrows to a ChordPattern.
              pattern: form.chordPattern === 'as-written' ? 'Block' : form.chordPattern,
              inversion: form.chordInversion,
              octaveShift: form.chordOctave,
            },
      guideTones: form.guideTones ? { octaveShift: form.guideOctave } : null,
      doubling: form.doubleVocal ? { voiceId: 'Vocal', semitones: 12 * form.doubleOctave } : null,
      groove:
        form.swing > 0 || form.humanize > 0
          ? {
              swing: form.swing / 100,
              swingUnit: form.swingUnit,
              includeDrums: !form.straightDrums,
              humanizeTimingMs: (form.humanize / 100) * maxHumanizeTimingMs,
              humanizeVelocity: Math.round((form.humanize / 100) * maxHumanizeVelocity),
            }
          : null,
      mono: form.mono ? { legato: form.legato } : null,
      countIn: form.countIn > 0 ? { bars: form.countIn, click: form.countInClick } : null,
    },
    midiChannels: chosen(form.channels),
    midiPrograms: chosen(form.programs),
  }
}

/** Only tracks the user actually set a number for; zero means "leave it to the converter". */
function chosen(values: Record<string, number>): Record<string, number> {
  return Object.fromEntries(Object.entries(values).filter(([, value]) => value > 0))
}

const storageKey = 'yue-ui.logic.options'

/** The last used parameters, so repeated conversions start from the same settings. */
export function loadFormState(): FormState {
  try {
    const stored = localStorage.getItem(storageKey)
    if (stored) {
      const state = { ...defaultFormState(), ...(JSON.parse(stored) as Partial<FormState>) }
      // Settings saved before drum patterns existed stored the drum switch as a boolean.
      const drums = state.drums as unknown
      if (typeof drums === 'boolean') {
        state.drums = drums ? 'FourOnTheFloor' : 'off'
      }
      return state
    }
  } catch {
    // Unavailable or corrupt storage: start with the defaults.
  }
  return defaultFormState()
}

export function clearFormState(): void {
  try {
    localStorage.removeItem(storageKey)
  } catch {
    // Nothing stored or storage unavailable.
  }
}

export function saveFormState(form: FormState): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(form))
  } catch {
    // Remembering the parameters is a convenience only.
  }
}

const routingKey = 'yue-ui.logic.routing'

/**
 * The preview's MIDI routing, kept per track name. A hardware setup has its instruments on fixed ports and
 * channels; having to set them again on every reload would make the preview useless for that.
 */
export function loadRoutings(ids: string[], fallback: Routing[]): Routing[] {
  try {
    const stored = localStorage.getItem(routingKey)
    if (stored) {
      const saved = JSON.parse(stored) as Record<string, Routing>
      return ids.map((id, index) => {
        const entry = saved[id]
        return entry && typeof entry.channel === 'number' && typeof entry.output === 'string'
          ? { output: entry.output, channel: entry.channel, muted: Boolean(entry.muted) }
          : fallback[index]!
      })
    }
  } catch {
    // Unavailable or corrupt storage: start from the defaults.
  }
  return fallback
}

export function saveRoutings(ids: string[], routings: Routing[]): void {
  try {
    const stored = localStorage.getItem(routingKey)
    const saved = stored ? (JSON.parse(stored) as Record<string, Routing>) : {}
    ids.forEach((id, index) => {
      const routing = routings[index]
      if (routing) {
        saved[id] = routing
      }
    })
    localStorage.setItem(routingKey, JSON.stringify(saved))
  } catch {
    // Remembering the routing is a convenience only.
  }
}

const recordingKey = 'yue-ui.logic.recording'

/** Whether the preview shows and plays the song's recording, and how loud; per browser, like the routing. */
export interface RecordingSettings {
  show: boolean
  /** Linear gain, 0 to 1. */
  volume: number
}

export function loadRecordingSettings(): RecordingSettings {
  try {
    const saved = JSON.parse(localStorage.getItem(recordingKey) ?? 'null') as Partial<RecordingSettings> | null
    if (saved) {
      return {
        show: saved.show !== false,
        volume: typeof saved.volume === 'number' ? Math.min(1, Math.max(0, saved.volume)) : 0.8,
      }
    }
  } catch {
    // Unavailable or corrupt storage: start from the defaults.
  }
  return { show: true, volume: 0.8 }
}

export function saveRecordingSettings(settings: RecordingSettings): void {
  try {
    localStorage.setItem(recordingKey, JSON.stringify(settings))
  } catch {
    // Remembering it is a convenience only.
  }
}
