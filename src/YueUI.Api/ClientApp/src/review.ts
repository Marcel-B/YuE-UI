import { ref, watch } from 'vue'
import type { RunInfo, SongInfo } from './types'

/**
 * Tidying up by rating: songs nobody rated a while after they were made are collected for another listen, to be rated
 * or deleted. Nothing is deleted by itself; a song that was just made has not been heard yet, hence the age.
 */
export const reviewDayChoices: readonly number[] = [3, 7, 14, 30]
const storageKey = 'yue-ui.reviewDays'

function initialDays(): number {
  try {
    const stored = Number(localStorage.getItem(storageKey))
    if (reviewDayChoices.includes(stored)) {
      return stored
    }
  } catch {
    // Storage may be blocked; a week it is then.
  }
  return 7
}

/** Days a song may stay unrated before it is offered; remembered per browser like the library's order. */
export const reviewDays = ref(initialDays())

watch(reviewDays, (value) => {
  try {
    localStorage.setItem(storageKey, String(value))
  } catch {
    // Remembering the choice is a convenience only.
  }
})

const dayMs = 24 * 60 * 60 * 1000

/**
 * A song to look at again: no stars and its run older than `days`. A run without a date (a folder name the worker's
 * pattern did not date) is never offered, since its age is unknown.
 */
export function needsReview(
  run: RunInfo,
  song: SongInfo,
  ratings: Record<string, number>,
  days: number,
  now: number = Date.now(),
): boolean {
  if (ratings[song.id] || !run.createdAt) {
    return false
  }
  const created = Date.parse(run.createdAt)
  return !Number.isNaN(created) && now - created >= days * dayMs
}

/** How many songs wait to be rated or deleted, for the menu's badge and the library's hint. */
export function reviewCount(runs: RunInfo[], ratings: Record<string, number>, days: number, now?: number): number {
  return runs.reduce(
    (sum, run) => sum + run.songs.filter((song) => needsReview(run, song, ratings, days, now)).length,
    0,
  )
}
