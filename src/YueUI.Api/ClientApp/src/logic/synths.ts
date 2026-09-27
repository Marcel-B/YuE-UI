import { ref } from 'vue'
import {
  listSynthPresets,
  listTrackSynths,
  putSynthPreset,
  putTrackSynth,
  removeSynthPreset,
  removeTrackSynth,
} from './api'
import type { SynthPatch } from './synth'

/**
 * The browser synthesizer's sounds as the server keeps them: one per track that was given its own, and named ones to
 * reuse. Held here once for the page, so the preview's player and the editor read the same state. Patches stay
 * unchecked JSON until they are played or edited, where `normalizePatch` fills in what the track's kind needs.
 */

export interface TrackSound {
  patch: unknown
  /** The preset the sound was taken from; it may have been changed since, the track keeps its copy. */
  preset: string | null
}

export interface NamedSound {
  name: string
  patch: unknown
}

/** Keyed by track name in upper case, since the server matches track names without regard to case. */
export const trackSounds = ref<Record<string, TrackSound>>({})
export const namedSounds = ref<NamedSound[]>([])
export const soundsError = ref<string | null>(null)

let loading: Promise<void> | null = null

export function trackKey(track: string): string {
  return track.toUpperCase()
}

function failed(caught: unknown): void {
  soundsError.value = caught instanceof Error ? caught.message : String(caught)
}

/** Loads both lists once; the page calls it when the preview first opens. */
export function loadSounds(): Promise<void> {
  loading ??= (async () => {
    try {
      const [tracks, presets] = await Promise.all([listTrackSynths(), listSynthPresets()])
      trackSounds.value = Object.fromEntries(
        tracks.map((entry) => [trackKey(entry.track), { patch: entry.patch, preset: entry.preset }]),
      )
      namedSounds.value = presets.map((entry) => ({ name: entry.name, patch: entry.patch }))
      soundsError.value = null
    } catch (caught) {
      // The preview still plays, on the default sounds.
      failed(caught)
      loading = null
    }
  })()
  return loading
}

const pending = new Map<string, number>()

/**
 * Gives the track its sound at once and saves it shortly after, so dragging a slider sends one request rather than
 * one per step.
 */
export function setTrackSound(track: string, patch: SynthPatch, preset: string | null): void {
  const key = trackKey(track)
  trackSounds.value = { ...trackSounds.value, [key]: { patch, preset } }
  window.clearTimeout(pending.get(key))
  pending.set(
    key,
    window.setTimeout(() => {
      pending.delete(key)
      putTrackSynth(track, patch, preset).then(() => (soundsError.value = null), failed)
    }, 500),
  )
}

/** Back to the default sound of the track's kind. */
export async function resetTrackSound(track: string): Promise<void> {
  const key = trackKey(track)
  window.clearTimeout(pending.get(key))
  pending.delete(key)
  const next = { ...trackSounds.value }
  delete next[key]
  trackSounds.value = next
  try {
    await removeTrackSynth(track)
    soundsError.value = null
  } catch (caught) {
    failed(caught)
  }
}

export async function saveNamedSound(name: string, patch: SynthPatch): Promise<void> {
  try {
    await putSynthPreset(name, patch)
    namedSounds.value = (await listSynthPresets()).map((entry) => ({ name: entry.name, patch: entry.patch }))
    soundsError.value = null
  } catch (caught) {
    failed(caught)
  }
}

export async function deleteNamedSound(name: string): Promise<void> {
  try {
    await removeSynthPreset(name)
    namedSounds.value = namedSounds.value.filter((entry) => entry.name.toUpperCase() !== name.toUpperCase())
    soundsError.value = null
  } catch (caught) {
    failed(caught)
  }
}
