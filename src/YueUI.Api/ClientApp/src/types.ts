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
