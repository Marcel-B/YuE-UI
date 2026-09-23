import type { Cot, Engines, GenerateRequest, Quality } from './types'

/** The web form as the browser keeps it; `seed` stays text so an empty field means "random". */
export interface FormState {
  title: string
  style: string
  lyrics: string
  instrumental: boolean
  quality: Quality
  batch: number
  cot: Cot
  seed: string
  draftSteps: number
  engines: Engines | ''
  /** Longest song in seconds; the API wants tokens. */
  maxSeconds: number
  abc: string
}

export const tokensPerSecond = 25
/** The worker's limit of 9000 tokens. */
export const maxSongSeconds = 360
export const lengthChoices = [30, 60, 90, 120, 150, 180, 240, 300, maxSongSeconds]

const storageKey = 'yue-ui.form'

type AdvancedKey = 'cot' | 'seed' | 'draftSteps' | 'engines' | 'maxSeconds' | 'abc'

/** The advanced section, set to what the worker would do without them. */
export function defaultAdvanced(): Pick<FormState, AdvancedKey> {
  return {
    cot: 'full',
    seed: '',
    draftSteps: 8,
    engines: '',
    maxSeconds: maxSongSeconds,
    abc: '',
  }
}

export function defaultFormState(): FormState {
  return {
    title: '',
    style: '',
    lyrics: '',
    instrumental: false,
    quality: 'draft',
    batch: 1,
    ...defaultAdvanced(),
  }
}

/** The advanced section is collapsed and kept across visits, so a forgotten seed or score must stay visible. */
export function advancedChanged(form: FormState): boolean {
  const defaults = defaultAdvanced()
  return (Object.keys(defaults) as AdvancedKey[]).some((key) =>
    typeof defaults[key] === 'string' ? String(form[key]).trim() !== defaults[key] : form[key] !== defaults[key],
  )
}

/** The last form, so a phone that reloads the page (or a second visit) keeps a half-written song. */
export function loadFormState(): FormState {
  try {
    const stored = localStorage.getItem(storageKey)
    if (stored) {
      return { ...defaultFormState(), ...(JSON.parse(stored) as Partial<FormState>) }
    }
  } catch {
    // Unreadable or blocked storage: start fresh.
  }
  return defaultFormState()
}

export function saveFormState(form: FormState): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(form))
  } catch {
    // Remembering the form is a convenience only.
  }
}

export function toGenerateRequest(form: FormState): GenerateRequest {
  const seed = form.seed.trim() === '' ? null : Number.parseInt(form.seed, 10)
  return {
    title: form.title.trim(),
    style: form.style.trim(),
    lyrics: form.lyrics.trim(),
    instrumental: form.instrumental,
    quality: form.quality,
    batch: form.batch,
    cot: form.cot,
    seed: seed !== null && Number.isFinite(seed) ? seed : null,
    engines: form.engines === '' ? null : form.engines,
    draftSteps: form.quality === 'draft' ? form.draftSteps : null,
    maxTokens: form.maxSeconds < maxSongSeconds ? form.maxSeconds * tokensPerSecond : null,
    abc: form.abc.trim() === '' ? null : form.abc.trim(),
  }
}

/** YuE2's examples/score.abc: the melody and chords for its "City Lights" lyrics (quoted in i18n's lyricsMore). */
export const exampleScore = `X:1
T:
M:4/4
L:1/16
Q:1/4=88
V: Vocal clef=treble name="Vocal Melody" snm="Vocal"
V: Ins clef=treble name="Ins Melody" snm="Inst."
K:C
% verse
V: Vocal
"C"E2G2A2G2E2D2C4|"G"D2E2G2E2D2C2D4|"Am"E2G2A2c2B2A2G4|"F"F2E2D2E2G2E2C4|
V: Ins
Z4|
% chorus
V: Vocal
"C"G2A2c2B2A2G2E4|"F"F2A2G2E2D2E2G4|"G"A2c2B2A2G2E2D4|"C"E2G2A2G2E2D2C4|
V: Ins
Z4|`
