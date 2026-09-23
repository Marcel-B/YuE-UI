// Mirrors the API's JSON (src/YueUI.Api: Worker/WorkerModels.cs, Library/SongLibrary.cs, GenerateRequest.cs) by hand.

export type WorkerStatus = 'stopped' | 'starting' | 'ready'

/** The worker's stages, in order; the last three end a song. */
export type Stage = 'queued' | 'planning' | 'tokens' | 'synth' | 'decode' | 'ready' | 'failed' | 'cancelled'

export interface WorkerInfo {
  status: WorkerStatus
  /** Some song is queued or running. */
  busy: boolean
  /** The YuE Studio app is open, with a worker (and a model) of its own. */
  studioRunning: boolean
  lastError: string | null
  /**
   * The last worker that started took YueUI's extra fields (sampling, full-quality steps, songs over six minutes);
   * null until one has started, false when a YuE Studio update left them without effect.
   */
  extensions: boolean | null
}

export interface SongState {
  /** `run/songN`, as in the library. */
  id: string
  run: string
  title: string
  index: number
  seed: number | null
  stage: Stage
  detail: string
  /** Progress of the current stage, 0–1. */
  fraction: number
  engine: string | null
  seconds: number | null
  quality: Quality | null
  message: string | null
  updatedAt: string
  finished: boolean
}

export interface LogEntry {
  time: string
  level: 'info' | 'error' | 'stderr'
  message: string
}

export interface StatusSnapshot {
  worker: WorkerInfo
  songs: SongState[]
  log: LogEntry[]
  transcriptions: TranscriptionState[]
}

/** melody-full: the Vocal and Ins melodies; melody-vocal: only the sung one. */
export type TranscriptionTask = 'melody-full' | 'melody-vocal'

/** A recording going through SheetSage2 (Worker/WorkerModels.cs). */
export interface TranscriptionState {
  id: string
  fileName: string
  task: TranscriptionTask
  stage: 'starting' | 'progress' | 'done' | 'failed' | 'cancelled'
  /** 0–1, null while SheetSage2 only says it is still busy. */
  fraction: number | null
  detail: string
  abc: string | null
  warnings: string[]
  /** The folder in the transcription list, once done. */
  result: string | null
  message: string | null
  /** busy, no_env, afconvert, abc_error or crash */
  code: string | null
  updatedAt: string
  finished: boolean
}

/** A finished transcription on disk (Library/TranscriptionLibrary.cs). */
export interface TranscriptionInfo {
  id: string
  sourceName: string
  task: TranscriptionTask | null
  createdAt: string | null
  warnings: string[]
}

export interface TranscriptionList {
  /** SheetSage2's environment exists (YuE Studio installs it). */
  installed: boolean
  items: TranscriptionInfo[]
}

export type Quality = 'draft' | 'full'

export type Cot = 'full' | 'melody' | 'off'

export type Engines = 'gpu' | 'gpu+ane'

export interface GenerateRequest {
  style: string
  lyrics: string
  title: string
  batch: number
  quality: Quality
  cot: Cot
  /** Null for a random seed. */
  seed: number | null
  instrumental: boolean
  /** Null leaves the choice to the worker (Neural Engine for full quality). */
  engines: Engines | null
  draftSteps: number | null
  /** Upper limit of song tokens (25 per second of audio, 200–9000); null for the worker's 9000. */
  maxTokens: number | null
  /** A score in ABC notation instead of the model's own plan; needs `cot` "full" or "melody". */
  abc: string | null
  /** Synthesis steps at full quality, 1–64; null for the model's 32. */
  fullSteps: number | null
  abcSampling: SamplingOverrides | null
  semanticSampling: SamplingOverrides | null
}

/** Changes to one of the model's sampling settings; a missing value keeps the model's. */
export interface SamplingOverrides {
  temperature?: number
  topP?: number
  topK?: number
  repetitionPenalty?: number
  penaltyWindow?: number
}

export interface SongInfo {
  id: string
  index: number
  seed: number | null
  quality: Quality | null
  seconds: number | null
  hasAudio: boolean
  hasScore: boolean
  /** The tokens are saved, so the song can be synthesized again (a draft at full quality). */
  canRender: boolean
}

export interface RunInfo {
  id: string
  title: string
  createdAt: string | null
  style: string
  lyrics: string
  songs: SongInfo[]
}
