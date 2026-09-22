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
}

const storageKey = 'yue-ui.form'

export function defaultFormState(): FormState {
  return {
    title: '',
    style: '',
    lyrics: '',
    instrumental: false,
    quality: 'draft',
    batch: 1,
    cot: 'full',
    seed: '',
    // The worker's own default.
    draftSteps: 8,
    engines: '',
  }
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
  }
}
