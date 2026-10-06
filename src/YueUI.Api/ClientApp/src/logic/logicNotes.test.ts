import { describe, expect, it } from 'vitest'
import { logicNotes } from './logicNotes'
import type { Diagnostic } from './types'

const warning = (code: string, message: string): Diagnostic => ({
  severity: 'Warning',
  code,
  message,
  line: null,
  column: null,
})

const missing = (voice: string) =>
  warning(
    'YTL053',
    `The Logic template has no track named '${voice}', so voice '${voice}' is only in the MIDI file. Its tracks are: Ins, Vocal. A template saved with a track of that name takes it as well.`,
  )

describe('logicNotes', () => {
  it('lists every voice without a template track in one note', () => {
    expect(logicNotes([missing('Harmony 3rd up'), missing('Harmony Alto')])).toEqual([
      { kind: 'midiOnly', voices: ['Harmony 3rd up', 'Harmony Alto'] },
    ])
  })

  it('reads track, output and instrument of an unknown output', () => {
    const diagnostic = warning(
      'YTL055',
      "Track 'Vocal': the MIDI output 'FM-1' of instrument 'M-VAVE FM-1' is not among the outputs the Logic template knows (Scarlett 8i6 USB), so the track keeps the template's instrument. A template saved with that interface connected takes it as well.",
    )
    expect(logicNotes([diagnostic])).toEqual([
      { kind: 'unknownOutput', track: 'Vocal', port: 'FM-1', instrument: 'M-VAVE FM-1' },
    ])
  })

  it('keeps any other warning as it is', () => {
    const diagnostic = warning(
      'YTL053',
      "Track 'Drums' lies on 'Aux 4' in the Logic template; instrument 'TR-8' could not be put on it.",
    )
    expect(logicNotes([diagnostic])).toEqual([{ kind: 'other', diagnostic }])
  })
})
