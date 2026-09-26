import { ref } from 'vue'
import { rateSong } from './api'
import type { RunInfo } from './types'

/**
 * Stars by song id, for the library and the player alike. The server keeps them (the phone and the Mac see the same);
 * this copy changes at once on a click and goes back if the server refuses. A song without rating has no entry.
 */
export const ratings = ref<Record<string, number>>({})

/** Takes the ratings of a freshly loaded library, which every browser reloads after a change anywhere. */
export function setRatings(runs: RunInfo[]): void {
  ratings.value = Object.fromEntries(
    runs.flatMap((run) => run.songs.flatMap((song) => (song.rating ? [[song.id, song.rating] as const] : []))),
  )
}

/** Undefined while not rated, which is how PrimeVue's Rating shows no stars. */
export function ratingOf(songId: string): number | undefined {
  return ratings.value[songId]
}

/** 1 to 5 stars, or null to take the rating away. */
export async function rate(songId: string, rating: number | null): Promise<void> {
  const before = ratings.value
  const { [songId]: _, ...others } = before
  ratings.value = rating ? { ...others, [songId]: rating } : others
  try {
    await rateSong(songId, rating)
  } catch (caught) {
    ratings.value = before
    throw caught
  }
}
