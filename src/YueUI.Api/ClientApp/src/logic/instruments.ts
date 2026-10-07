import { AUDIO_OUTPUT, type MidiPort, type Routing } from './midiPlayer'
import {
  DRUMS,
  GENERAL_MIDI_DRUMS,
  type Assignments,
  type ConversionOptions,
  type DrumNotes,
  type Instrument,
  type LogicInstrument,
} from './types'

/**
 * Instruments are what the user calls a MIDI port and channel: "Mother32" rather than "MIDI4x4 Midi Out 1,
 * channel 12". They live on the server, as does which track plays which. This module turns that into what
 * the preview and the exports work with.
 */

/** The instrument a track is assigned to, or null when it has none or the instrument is gone. */
export function instrumentOf(track: string, assignments: Assignments, instruments: Instrument[]): Instrument | null {
  const id = assignments[track]
  return id === undefined ? null : (instruments.find((instrument) => instrument.id === id) ?? null)
}

/**
 * The port an instrument names, among those the browser currently offers. Ports are matched by name because
 * the id Web MIDI gives them differs between machines, while the name is what the device is called.
 */
export function resolvePort(ports: MidiPort[], portName: string): MidiPort | null {
  const wanted = portName.trim()
  return ports.find((port) => port.name.trim() === wanted) ?? null
}

/** Where a track really goes: its instrument, if it has one, else the routing chosen by hand. */
export interface EffectiveRouting {
  routing: Routing
  instrument: Instrument | null
  /** The instrument's port, when it is connected; a null with an instrument means it is not. */
  port: MidiPort | null
}

/**
 * An instrument overrides the port and channel of a routing but not whether the track is muted. An instrument
 * whose port is unplugged plays through the browser sound, so that the preview still sounds.
 */
export function effectiveRouting(routing: Routing, instrument: Instrument | null, ports: MidiPort[]): EffectiveRouting {
  if (!instrument) {
    return { routing, instrument: null, port: null }
  }
  const port = resolvePort(ports, instrument.port)
  return {
    routing: { output: port?.id ?? AUDIO_OUTPUT, channel: instrument.channel - 1, muted: routing.muted },
    instrument,
    port,
  }
}

/** Every assigned track with its instrument; assignments to instruments that no longer exist are left out. */
function assigned(assignments: Assignments, instruments: Instrument[]): [string, Instrument][] {
  return Object.keys(assignments).flatMap((track) => {
    const instrument = instrumentOf(track, assignments, instruments)
    return instrument ? [[track, instrument] as [string, Instrument]] : []
  })
}

/**
 * The instrument's channel wins over the one chosen under "Advanced", so that the MIDI file, the preview and
 * the Logic project all play a track where its instrument listens.
 */
export function withInstrumentChannels(
  options: ConversionOptions,
  assignments: Assignments,
  instruments: Instrument[],
): ConversionOptions {
  const midiChannels = { ...options.midiChannels }
  for (const [track, instrument] of assigned(assignments, instruments)) {
    midiChannels[track] = instrument.channel
  }
  return { ...options, midiChannels }
}

/** The drum machine a track plays, or null when its instrument is a synthesizer or it has none. */
function drumMachineOf(track: string, assignments: Assignments, instruments: Instrument[]): DrumNotes | null {
  const instrument = instrumentOf(track, assignments, instruments)
  return instrument?.kind === 'DrumMachine' ? instrument.drums : null
}

/** Which drum track carries which drums of the kit, as the split kit sorts them. */
const DRUM_TRACKS: Record<string, readonly (keyof DrumNotes)[]> = {
  Kick: ['kick'],
  Snare: ['snare'],
  HiHat: ['closedHiHat', 'openHiHat'],
  Crash: ['crash'],
}

/**
 * The note a drum track's test plays: the drum the track carries (a combined `Drums` track the snare), on the
 * note its drum machine has for it, else General MIDI's, which the browser kit also plays.
 */
export function drumTestNote(track: string, instrument: Instrument | null): number {
  const drum = DRUM_TRACKS[track]?.[0] ?? 'snare'
  return (instrument?.kind === 'DrumMachine' ? instrument.drums?.[drum] : undefined) ?? GENERAL_MIDI_DRUMS[drum]
}

/**
 * The notes the drums are generated on, taken from the drum machine each drum track plays: with one drum
 * track from the machine on `Drums`; with a split kit each track from its own, so a kick on one machine and a
 * snare on another both come out right. A track without a drum machine keeps General MIDI. The count-in click
 * takes the clap of the machine the click lands on, which is the first drum track. Null when no drum machine
 * is involved, so the options say nothing about notes then.
 */
export function withDrumNotes(
  options: ConversionOptions,
  assignments: Assignments,
  instruments: Instrument[],
): ConversionOptions {
  const drums = options.arrangement.drums
  if (!drums) {
    return options
  }

  let notes: DrumNotes | null = null
  if (!drums.separateTracks) {
    notes = drumMachineOf('Drums', assignments, instruments)
  } else {
    const machines = Object.entries(DRUM_TRACKS).map(
      ([track, roles]) => [roles, drumMachineOf(track, assignments, instruments)] as const,
    )
    if (machines.some(([, machine]) => machine !== null)) {
      const merged: DrumNotes = { ...GENERAL_MIDI_DRUMS }
      for (const [roles, machine] of machines) {
        if (machine) {
          for (const role of roles) {
            merged[role] = machine[role]
          }
        }
      }
      // The click of a count-in goes on the first drum track, the kick's.
      merged.clap = drumMachineOf('Kick', assignments, instruments)?.clap ?? GENERAL_MIDI_DRUMS.clap
      notes = merged
    }
  }

  if (!notes) {
    return options
  }
  const complete = Object.fromEntries(DRUMS.map((drum) => [drum, notes[drum]])) as unknown as DrumNotes
  return { ...options, arrangement: { ...options.arrangement, drums: { ...drums, notes: complete } } }
}

/** What the Logic export needs to know per track: the instrument's name, port and channel. */
export function instrumentsForExport(
  assignments: Assignments,
  instruments: Instrument[],
): Record<string, LogicInstrument> {
  return Object.fromEntries(
    assigned(assignments, instruments).map(([track, { name, port, channel }]) => [track, { name, port, channel }]),
  )
}
