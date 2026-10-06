import { describe, expect, test } from 'vitest'
import { highlight, matchesRun, matchingLines, parseQuery } from './search'
import { run } from './test/fixtures'

describe('parseQuery', () => {
  test('folds case and accents and keeps a quoted phrase whole', () => {
    expect(parseQuery('  Träume  "City   Lights" café ')).toEqual(['traume', 'city lights', 'cafe'])
  })

  test('an unclosed quote runs to the end, an empty one is dropped', () => {
    expect(parseQuery('a "" "b c')).toEqual(['a', 'b c'])
  })
})

describe('matchesRun', () => {
  const song = run('r', {
    title: 'Nachtzug',
    originalTitle: 'night train',
    style: 'Synthwave, male vocals',
    lyrics: '[verse]\nUnder the city\nlights we go',
  })

  test('every term must occur, in title, original title or style', () => {
    expect(matchesRun(song, parseQuery('synthwave nacht'), 'titleStyle')).toBe(true)
    expect(matchesRun(song, parseQuery('train'), 'titleStyle')).toBe(true)
    expect(matchesRun(song, parseQuery('synthwave jazz'), 'titleStyle')).toBe(false)
  })

  test('title and lyrics are never searched together', () => {
    expect(matchesRun(song, parseQuery('city'), 'titleStyle')).toBe(false)
    expect(matchesRun(song, parseQuery('nachtzug'), 'lyrics')).toBe(false)
  })

  test('a phrase finds a line the lyrics wrap', () => {
    expect(matchesRun(song, parseQuery('"city lights"'), 'lyrics')).toBe(true)
  })

  test('no terms match everything', () => {
    expect(matchesRun(song, [], 'lyrics')).toBe(true)
  })
})

describe('highlight', () => {
  test('marks the original characters, accents included', () => {
    expect(highlight('Süße Träume', ['traum'])).toEqual([
      { text: 'Süße ', hit: false },
      { text: 'Träum', hit: true },
      { text: 'e', hit: false },
    ])
  })

  test('a decomposed letter is marked whole', () => {
    const line = 'Träume'
    expect(highlight(line, ['traume'])).toEqual([{ text: line, hit: true }])
  })
})

describe('matchingLines', () => {
  test('skips section tags and stops at the limit', () => {
    const lyrics = '[love]\nlove one\n\nno\nlove two\nlove three'
    const lines = matchingLines(lyrics, ['love'], 2)
    expect(lines.map((line) => line.map((part) => part.text).join(''))).toEqual(['love one', 'love two'])
  })
})
