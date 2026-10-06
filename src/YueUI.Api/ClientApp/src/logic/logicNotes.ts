import type { Diagnostic } from './types'

/**
 * The Logic export's warnings, shortened for the page. The server's messages are English and long, and backing
 * vocals bring one YTL053 per voice, so voices without a template track become one note listing them, and an
 * output the template does not know one short note per track. Anything else is shown as the server wrote it.
 */
export type LogicNote =
  | { kind: 'midiOnly'; voices: string[] }
  | { kind: 'unknownOutput'; track: string; port: string; instrument: string }
  | { kind: 'other'; diagnostic: Diagnostic }

const MIDI_ONLY = /so voice '(.+?)' is only in the MIDI file/
const UNKNOWN_OUTPUT = /^Track '(.+?)': the MIDI output '(.+?)' of instrument '(.+?)' is not among/

export function logicNotes(diagnostics: Diagnostic[]): LogicNote[] {
  const notes: LogicNote[] = []
  let midiOnly: { kind: 'midiOnly'; voices: string[] } | null = null
  for (const diagnostic of diagnostics) {
    const voice = diagnostic.code === 'YTL053' ? MIDI_ONLY.exec(diagnostic.message) : null
    if (voice) {
      if (!midiOnly) {
        midiOnly = { kind: 'midiOnly', voices: [] }
        notes.push(midiOnly)
      }
      midiOnly.voices.push(voice[1] ?? '')
      continue
    }
    const output = diagnostic.code === 'YTL055' ? UNKNOWN_OUTPUT.exec(diagnostic.message) : null
    notes.push(
      output
        ? { kind: 'unknownOutput', track: output[1] ?? '', port: output[2] ?? '', instrument: output[3] ?? '' }
        : { kind: 'other', diagnostic },
    )
  }
  return notes
}
