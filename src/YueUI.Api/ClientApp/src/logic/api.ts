import type {
  Assignments,
  ConversionOptions,
  ConversionResult,
  Diagnostic,
  Instrument,
  InstrumentInput,
  LogicInstrument,
} from './types'

/**
 * The Logic page's calls, ported from yue-to-logic-pro. Kept apart from ../api.ts, which stays the only place YuE
 * UI's own features talk to /api: these routes carry YueToLogic.Core's contract (PascalCase enums, the MIDI as
 * base64) rather than YuE UI's records.
 */

const apiBase = import.meta.env.VITE_API_BASE ?? ''

/** A request the backend refused or could not answer; `status` is 0 for network errors. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message)
  }
}

/**
 * Where the score comes from: a song of the library, which the server reads score.abc and audio.flac of itself,
 * or a score.abc the user uploads, optionally with its audio.flac.
 */
export type ScoreSource = { song: string } | { file: File; audio: File | null }

function appendSource(form: FormData, source: ScoreSource, withAudio: boolean): void {
  if ('song' in source) {
    form.append('song', source.song)
  } else {
    form.append('file', source.file)
    if (withAudio && source.audio) {
      form.append('audio', source.audio)
    }
  }
}

/**
 * Sends the score and options to the backend. Resolves with the result for both a successful (200) and an
 * unconvertible (422) score, because both carry diagnostics worth showing; rejects with an ApiError otherwise.
 */
export async function convertScore(
  source: ScoreSource,
  options: ConversionOptions,
  signal?: AbortSignal,
): Promise<ConversionResult> {
  const form = new FormData()
  appendSource(form, source, false)
  form.append('options', JSON.stringify(options))

  const response = await post('/api/logic/convert', form, signal)
  if (response.ok || response.status === 422) {
    return (await response.json()) as ConversionResult
  }
  throw await problem(response)
}

/** The Logic project could not be built; the diagnostics say why (e.g. not a FLAC file). */
export class LogicExportError extends Error {
  constructor(readonly diagnostics: Diagnostic[]) {
    super(diagnostics.map((d) => d.message).join(' '))
  }
}

export interface LogicExport {
  zip: Blob
  fileName: string
  /** Warnings such as an audio file whose length does not match the score. */
  warnings: Diagnostic[]
}

/** Builds a zipped Logic Pro project (.logicx) from the score, the options and, if there is one, its audio.flac. */
export async function exportLogicProject(
  source: ScoreSource,
  options: ConversionOptions,
  name: string,
  /** One region per song section instead of one per track; a property of the project, not of the score. */
  splitSections: boolean,
  /** The instrument each track plays; the track then sits on its channel and is named after it. */
  instruments: Record<string, LogicInstrument> = {},
  signal?: AbortSignal,
): Promise<LogicExport> {
  const form = new FormData()
  appendSource(form, source, true)
  form.append('options', JSON.stringify(options))
  form.append('name', name)
  form.append('splitSections', String(splitSections))
  if (Object.keys(instruments).length > 0) {
    form.append('instruments', JSON.stringify(instruments))
  }

  const response = await post('/api/logic/export', form, signal)
  if (response.status === 422) {
    throw new LogicExportError(((await response.json()) as ConversionResult).diagnostics)
  }
  if (!response.ok) {
    throw await problem(response)
  }

  const header = response.headers.get('X-YueToLogic-Diagnostics')
  return {
    zip: await response.blob(),
    fileName: `${name}.logicx.zip`,
    warnings: header ? (JSON.parse(header) as Diagnostic[]) : [],
  }
}

// ---- Presets -----------------------------------------------------------------------------------

/** A preset as the server keeps it; the form is whatever the interface saved, checked on the way in. */
export interface StoredPreset {
  id: number
  name: string
  form: unknown
  updatedAt: string
}

export async function listPresets(): Promise<StoredPreset[]> {
  return (await request('/api/logic/presets')).json() as Promise<StoredPreset[]>
}

/** Saves the form under the name, replacing a preset of that name. */
export async function putPreset(name: string, form: unknown): Promise<StoredPreset> {
  return (
    await request(`/api/logic/presets/${encodeURIComponent(name)}`, json('PUT', { form }))
  ).json() as Promise<StoredPreset>
}

export async function removePreset(name: string): Promise<void> {
  await request(`/api/logic/presets/${encodeURIComponent(name)}`, { method: 'DELETE' })
}

// ---- Instruments -------------------------------------------------------------------------------

export async function listInstruments(): Promise<Instrument[]> {
  return (await request('/api/instruments')).json() as Promise<Instrument[]>
}

export async function createInstrument(input: InstrumentInput): Promise<Instrument> {
  return (await request('/api/instruments', json('POST', input))).json() as Promise<Instrument>
}

export async function updateInstrument(id: number, input: InstrumentInput): Promise<Instrument> {
  return (await request(`/api/instruments/${id}`, json('PUT', input))).json() as Promise<Instrument>
}

/** Removes the instrument; the server drops the assignments of tracks to it as well. */
export async function deleteInstrument(id: number): Promise<void> {
  await request(`/api/instruments/${id}`, { method: 'DELETE' })
}

export async function listAssignments(): Promise<Assignments> {
  return (await request('/api/instruments/assignments')).json() as Promise<Assignments>
}

/** Gives a track an instrument, or takes it away with `null`. */
export async function assignInstrument(track: string, instrumentId: number | null): Promise<void> {
  await request(`/api/instruments/assignments/${encodeURIComponent(track)}`, json('PUT', { instrumentId }))
}

function json(method: string, body: unknown): RequestInit {
  return { method, body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } }
}

/** A multipart POST whose answer the caller reads itself, since 422 carries a result rather than a problem. */
async function post(path: string, body: FormData, signal?: AbortSignal): Promise<Response> {
  try {
    return await fetch(`${apiBase}${path}`, { method: 'POST', body, signal })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }
    throw new ApiError('network', 0)
  }
}

/** A problem document's detail, or the status. */
async function problem(response: Response): Promise<ApiError> {
  const body = (await response.json().catch(() => null)) as { title?: string; detail?: string } | null
  return new ApiError(body?.detail ?? body?.title ?? `HTTP ${response.status}`, response.status)
}

/** A request whose failure is worth an ApiError. */
async function request(path: string, init: RequestInit = {}): Promise<Response> {
  let response: Response
  try {
    response = await fetch(`${apiBase}${path}`, init)
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }
    throw new ApiError('network', 0)
  }

  if (!response.ok) {
    throw await problem(response)
  }
  return response
}
