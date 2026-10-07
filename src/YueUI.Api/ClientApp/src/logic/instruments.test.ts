import { describe, expect, it } from 'vitest'
import { drumTestNote, withDrumNotes } from './instruments'
import { GENERAL_MIDI_DRUMS, type ConversionOptions, type Instrument } from './types'

const impact: Instrument = {
  id: 7,
  name: 'Drumbrute Impact',
  port: 'MIDI4x4 Midi Out 2',
  channel: 8,
  kind: 'DrumMachine',
  drums: { kick: 36, snare: 37, closedHiHat: 43, openHiHat: 44, crash: 41, clap: 38 },
}

describe('drumTestNote', () => {
  it('plays the drum of the track on the note its machine has for it', () => {
    expect(drumTestNote('Kick', impact)).toBe(36)
    expect(drumTestNote('Snare', impact)).toBe(37)
    expect(drumTestNote('HiHat', impact)).toBe(43)
    expect(drumTestNote('Crash', impact)).toBe(41)
  })

  it('falls back to General MIDI without a drum machine', () => {
    expect(drumTestNote('Crash', null)).toBe(GENERAL_MIDI_DRUMS.crash)
    expect(drumTestNote('Drums', null)).toBe(GENERAL_MIDI_DRUMS.snare)
  })
})

describe('withDrumNotes', () => {
  it('takes every split drum track from the machine it plays', () => {
    const options = {
      midiChannels: {},
      arrangement: { drums: { pattern: 'Basic', crashOnSections: true, separateTracks: true, notes: null } },
    } as unknown as ConversionOptions
    const assignments = { Kick: 7, Snare: 7, HiHat: 7, Crash: 7 }
    expect(withDrumNotes(options, assignments, [impact]).arrangement.drums?.notes).toEqual(impact.drums)
  })
})
