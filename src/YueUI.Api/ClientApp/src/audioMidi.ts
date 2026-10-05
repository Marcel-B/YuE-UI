import { ref, watch } from 'vue'
import { getAudioMidi } from './api'
import type { AudioMidiInfo, AudioMidiSettings } from './types'

/**
 * Audio to MIDI with Basic Pitch, shared by the transcription page (an uploaded recording) and the stems (a separated
 * track): the settings, kept per browser, and whether the Mac has Basic Pitch at all.
 */
const storageKey = 'yue-ui.audioMidi'

function load(): AudioMidiSettings {
  const defaults: AudioMidiSettings = { mono: true, quantize: false, bends: false, tempo: null }
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as Partial<AudioMidiSettings> | null
    return {
      mono: typeof saved?.mono === 'boolean' ? saved.mono : defaults.mono,
      quantize: typeof saved?.quantize === 'boolean' ? saved.quantize : defaults.quantize,
      bends: typeof saved?.bends === 'boolean' ? saved.bends : defaults.bends,
      tempo: typeof saved?.tempo === 'number' && saved.tempo >= 20 && saved.tempo <= 300 ? saved.tempo : null,
    }
  } catch {
    return defaults
  }
}

export const midiSettings = ref<AudioMidiSettings>(load())
watch(
  midiSettings,
  (value) => {
    try {
      localStorage.setItem(storageKey, JSON.stringify(value))
    } catch {
      // Remembering the choice is a convenience only.
    }
  },
  { deep: true },
)

/** Null until asked for; asked once per page load, which is when a deploy or an install could change it. */
export const midiInfo = ref<AudioMidiInfo | null>(null)
let asking: Promise<void> | null = null

export function loadMidiInfo(): Promise<void> {
  asking ??= getAudioMidi()
    .then((info) => void (midiInfo.value = info))
    .catch(() => {
      asking = null
    })
  return asking
}
