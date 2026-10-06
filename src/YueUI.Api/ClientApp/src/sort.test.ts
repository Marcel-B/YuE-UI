import { describe, expect, test } from 'vitest'
import { sortRuns } from './sort'
import { run, song } from './test/fixtures'

// The server's order: newest first.
const runs = [
  run('new', { songs: [song('new/song1', { seconds: 60 }), song('new/song2', { seconds: 200 })] }),
  run('mid', { songs: [song('mid/song1', { seconds: 120 })] }),
  run('old', { songs: [song('old/song1', { seconds: null }), song('old/song2', { seconds: 30 })] }),
]
const all = (r: (typeof runs)[number]) => r.songs
const ids = (sorted: ReturnType<typeof sortRuns>) => sorted.map((entry) => [entry.id, ...entry.songs.map((s) => s.id)])

describe('sortRuns', () => {
  test('newest keeps the order, oldest reverses it', () => {
    expect(sortRuns(runs, 'newest', {}, all).map((e) => e.id)).toEqual(['new', 'mid', 'old'])
    expect(sortRuns(runs, 'oldest', {}, all).map((e) => e.id)).toEqual(['old', 'mid', 'new'])
  })

  test('a run moves by its best song, which comes first in it', () => {
    const ratings = { 'new/song1': 3, 'old/song2': 5, 'mid/song1': 3 }
    expect(ids(sortRuns(runs, 'rating', ratings, all))).toEqual([
      ['old', 'old/song2', 'old/song1'],
      // Equal runs keep the server's order.
      ['new', 'new/song1', 'new/song2'],
      ['mid', 'mid/song1'],
    ])
  })

  test('a song without a length goes last either way', () => {
    expect(ids(sortRuns(runs, 'longest', {}, all))).toEqual([
      ['new', 'new/song2', 'new/song1'],
      ['mid', 'mid/song1'],
      ['old', 'old/song2', 'old/song1'],
    ])
    expect(ids(sortRuns(runs, 'shortest', {}, all))).toEqual([
      ['old', 'old/song2', 'old/song1'],
      ['new', 'new/song1', 'new/song2'],
      ['mid', 'mid/song1'],
    ])
  })

  test('a hidden song does not decide where its run goes', () => {
    const shown = (r: (typeof runs)[number]) => r.songs.filter((s) => s.id !== 'old/song2')
    const sorted = sortRuns(runs, 'rating', { 'old/song2': 5, 'mid/song1': 1 }, shown)
    expect(sorted.map((e) => e.id)).toEqual(['mid', 'new', 'old'])
    // The run itself stays whole.
    expect(sorted[2]!.run.songs).toHaveLength(2)
  })
})
