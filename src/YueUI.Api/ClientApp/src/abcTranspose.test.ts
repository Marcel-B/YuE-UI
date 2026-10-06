import { describe, expect, test } from 'vitest'
import { abcKey, changeMode, keyChoices, transposeAbc, transposeToKey, vocalPitches, vocalRange } from './abcTranspose'

// One bar that leans on YuE2's rule that an accidental holds for its letter in every octave until the bar line.
const score = 'X:1\nK:C\nV:Vocal\n"C" C D E F | ^F G ^f A |\n"G7" B c d2 |]'

describe('transposeAbc', () => {
  test('moves notes, chords and the key, and spells them in the new key', () => {
    expect(transposeAbc(score, 2)).toEqual({
      abc: 'X:1\nK:D\nV:Vocal\n"D" D E F G | ^G A g B |\n"A7" c d e2 |]',
      key: 'D',
    })
  })

  test('writes a natural where the new key or an earlier accidental in the bar would sharpen the note', () => {
    expect(transposeAbc(score, -1).abc).toBe('X:1\nK:B\nV:Vocal\n"B" B, C D E | =F ^F =f G |\n"F#7" A B c2 |]')
    expect(transposeAbc(score, 1).abc).toBe('X:1\nK:Db\nV:Vocal\n"Db" D E F G | =G A g B |\n"Ab7" c d e2 |]')
  })

  test('an octave keeps the spelling and moves the octave marks', () => {
    expect(transposeAbc(score, 12).abc).toBe('X:1\nK:C\nV:Vocal\n"C" c d e f | ^f g f\' a |\n"G7" b c\' d\'2 |]')
  })

  test('there and back sings the same notes; an accidental the bar rule already gives is left out', () => {
    for (const semitones of [1, 3, 5, 7, -2, -6]) {
      const back = transposeAbc(transposeAbc(score, semitones).abc, -semitones).abc
      expect(vocalPitches(back)).toEqual(vocalPitches(score))
    }
    expect(transposeAbc(transposeAbc(score, 2).abc, -2).abc).toBe(score.replace('^f', 'f'))
  })
})

describe('changeMode', () => {
  test('major to minor lowers third, sixth and seventh and the chords follow', () => {
    expect(changeMode('X:1\nK:D\nV:Vocal\n"D" D E F G | A B c d |')).toEqual({
      abc: 'X:1\nK:Dm\nV:Vocal\n"Dm" D E F G | A B c d |',
      key: 'Dm',
    })
  })
})

describe('transposeToKey', () => {
  test('into the other mode changes the mode, then takes the shorter way', () => {
    expect(transposeToKey(score, 'Am')).toEqual({
      abc: 'X:1\nK:Am\nV:Vocal\n"Am" A, B, C D | _E =E _e F |\n"Em7" G A B2 |]',
      key: 'Am',
    })
  })

  test('goes down at most six semitones and up at most five', () => {
    // G is seven up or five down: down it goes, an octave low below C.
    expect(vocalPitches(transposeToKey(score, 'G').abc)[0]).toBe(55)
    // F is five up.
    expect(vocalPitches(transposeToKey(score, 'F').abc)[0]).toBe(65)
  })

  test('offers all 24 keys', () => {
    const keys = keyChoices()
    expect(keys).toHaveLength(24)
    expect(new Set(keys).size).toBe(24)
  })
})

describe('the vocal range', () => {
  test('reads the Vocal voice with the bar rule, C4 being middle C', () => {
    expect(vocalPitches(score)).toEqual([60, 62, 64, 65, 66, 67, 78, 69, 71, 72, 74])
    expect(vocalRange(score)).toEqual({ low: 'C4', high: 'F♯5' })
  })

  test('is null without notes', () => {
    expect(vocalRange('X:1\nK:C\n')).toBeNull()
  })

  test('the key is read from the K: field', () => {
    expect(abcKey(score)).toBe('C')
    expect(abcKey('X:1\nT:none\n')).toBeNull()
  })
})
