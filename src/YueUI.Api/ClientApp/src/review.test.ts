import { describe, expect, test } from 'vitest'
import { needsReview, reviewCount } from './review'
import { run, song } from './test/fixtures'

const now = Date.parse('2026-10-06T12:00:00Z')
const day = 24 * 60 * 60 * 1000

describe('needsReview', () => {
  const week = run('r', { createdAt: new Date(now - 7 * day).toISOString() })
  const one = song('r/song1')

  test('an unrated song is offered once its run is old enough', () => {
    expect(needsReview(week, one, {}, 7, now)).toBe(true)
    expect(needsReview(week, one, {}, 8, now)).toBe(false)
  })

  test('a rated song or a run without a date never is', () => {
    expect(needsReview(week, one, { 'r/song1': 2 }, 7, now)).toBe(false)
    expect(needsReview(run('r', { createdAt: null }), one, {}, 0, now)).toBe(false)
    expect(needsReview(run('r', { createdAt: 'not a date' }), one, {}, 0, now)).toBe(false)
  })

  test('reviewCount counts songs, not runs', () => {
    const runs = [
      run('a', { createdAt: new Date(now - 30 * day).toISOString(), songs: [song('a/song1'), song('a/song2')] }),
      run('b', { createdAt: new Date(now - day).toISOString(), songs: [song('b/song1')] }),
    ]
    expect(reviewCount(runs, { 'a/song2': 4 }, 7, now)).toBe(1)
    expect(reviewCount(runs, {}, 1, now)).toBe(3)
  })
})
