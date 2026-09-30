import { ref } from 'vue'
import { listInstruments } from './api'
import type { Instrument } from './types'

/**
 * The MIDI instrument library, held once for the app: the instruments page edits it and the Logic page assigns tracks
 * to it, and both are kept mounted side by side, so a change on one is seen on the other without a reload.
 */
export const instruments = ref<Instrument[]>([])

/** Fetches the list anew, so ids and order are the server's; throws when the server cannot be reached. */
export async function reloadInstruments(): Promise<Instrument[]> {
  instruments.value = await listInstruments()
  return instruments.value
}
