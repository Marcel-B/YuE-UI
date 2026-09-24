import type { Cot, Engines, GenerateRequest, Quality, SamplingOverrides } from './types'

/** The web form as the browser keeps it; `seed` stays text so an empty field means "random". */
export interface FormState {
  title: string
  style: string
  lyrics: string
  /** Keywords for a lyrics draft; not sent with the song. */
  lyricsIdea: string
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
  fullSteps: number
  /** How the score is sampled. */
  abcSampling: Sampling
  /** How the song tokens are sampled. */
  semanticSampling: Sampling
}

/** One of the model's sampling settings, every value filled in. */
export interface Sampling {
  temperature: number
  topP: number
  topK: number
  repetitionPenalty: number
  penaltyWindow: number
}

export type SamplingPhase = 'abcSampling' | 'semanticSampling'

/** YuE2's GenerationConfig (src/yue2/protocol.py): the checkpoint's own values. */
export const defaultSampling: Record<SamplingPhase, Sampling> = {
  abcSampling: { temperature: 0.7, topP: 0.9, topK: 30, repetitionPenalty: 1.005, penaltyWindow: 100 },
  semanticSampling: { temperature: 1.0, topP: 0.95, topK: 100, repetitionPenalty: 1.2, penaltyWindow: 50 },
}

export const tokensPerSecond = 25
/** The model's default of 9000 tokens; longer songs need the worker extension. */
export const maxSongSeconds = 360
export const lengthChoices = [30, 60, 90, 120, 150, 180, 240, 300, maxSongSeconds, 420, 480, 540, 600]
export const defaultFullSteps = 32

const storageKey = 'yue-ui.form'

type AdvancedKey =
  'cot' | 'seed' | 'draftSteps' | 'fullSteps' | 'engines' | 'maxSeconds' | 'abc' | 'abcSampling' | 'semanticSampling'

/** The advanced section, set to what the worker would do without them. */
export function defaultAdvanced(): Pick<FormState, AdvancedKey> {
  return {
    cot: 'full',
    seed: '',
    draftSteps: 8,
    engines: '',
    maxSeconds: maxSongSeconds,
    abc: '',
    fullSteps: defaultFullSteps,
    abcSampling: { ...defaultSampling.abcSampling },
    semanticSampling: { ...defaultSampling.semanticSampling },
  }
}

export function defaultFormState(): FormState {
  return {
    title: '',
    style: '',
    lyrics: '',
    lyricsIdea: '',
    instrumental: false,
    quality: 'draft',
    batch: 1,
    ...defaultAdvanced(),
  }
}

/** A score and the planning chosen for it (a transcription wants "melody") belong together and outlast a reset. */
function hasScore(form: FormState): boolean {
  return form.abc.trim() !== ''
}

/** The advanced parameters back at their defaults; the score is not a parameter and stays, with its planning. */
export function resetAdvanced(form: FormState): FormState {
  return { ...form, ...defaultAdvanced(), abc: form.abc, cot: hasScore(form) ? form.cot : 'full' }
}

/**
 * Whether an advanced parameter differs from its default. The section is collapsed and kept across visits, so a
 * forgotten seed must stay visible; a score (with its planning) is shown on its own.
 */
export function advancedChanged(form: FormState): boolean {
  const defaults = defaultAdvanced()
  return (Object.keys(defaults) as AdvancedKey[]).some((key) => {
    if (key === 'abc' || (key === 'cot' && hasScore(form))) {
      return false
    }
    const value = form[key]
    const initial = defaults[key]
    if (typeof initial === 'string') {
      return String(value).trim() !== initial
    }
    return typeof initial === 'object' ? JSON.stringify(value) !== JSON.stringify(initial) : value !== initial
  })
}

/** Only what differs from the model's values, so unchanged songs still share token batches with others. */
export function samplingOverrides(phase: SamplingPhase, sampling: Sampling): SamplingOverrides | null {
  const overrides: SamplingOverrides = {}
  for (const key of Object.keys(sampling) as (keyof Sampling)[]) {
    if (Number.isFinite(sampling[key]) && sampling[key] !== defaultSampling[phase][key]) {
      overrides[key] = sampling[key]
    }
  }
  return Object.keys(overrides).length > 0 ? overrides : null
}

/** The last form, so a phone that reloads the page (or a second visit) keeps a half-written song. */
export function loadFormState(): FormState {
  try {
    const stored = localStorage.getItem(storageKey)
    if (stored) {
      const defaults = defaultFormState()
      const form = JSON.parse(stored) as Partial<FormState>
      // Nested, so a form kept before a value was added still gets it.
      return {
        ...defaults,
        ...form,
        abcSampling: { ...defaults.abcSampling, ...form.abcSampling },
        semanticSampling: { ...defaults.semanticSampling, ...form.semanticSampling },
      }
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
    fullSteps: form.quality === 'full' && form.fullSteps !== defaultFullSteps ? form.fullSteps : null,
    abcSampling: samplingOverrides('abcSampling', form.abcSampling),
    semanticSampling: samplingOverrides('semanticSampling', form.semanticSampling),
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
