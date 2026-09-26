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
  /** The stages so far, each with when it began, oldest first; the last one is `stage`. */
  stages: StageTime[]
  /** A render from saved tokens: no planning or composing ahead of it. */
  render: boolean
  finished: boolean
}

export interface StageTime {
  stage: Stage
  startedAt: string
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
  lyrics: LyricsState | null
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
  /** What the folder takes on disk. */
  bytes: number
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
  /** What the song folder takes on disk: audio, tokens and the worker's intermediate files. */
  bytes: number
  /** One to five stars given in this app, null while not rated. */
  rating: number | null
}

/**
 * What a song was generated with, from its request.json (Library/SongLibrary.cs); null where the file does not say,
 * e.g. for a song YuE Studio made.
 */
export interface SongRequest {
  title: string
  style: string
  lyrics: string
  instrumental: boolean | null
  quality: Quality | null
  cot: Cot | null
  seed: number | null
  engines: Engines | null
  draftSteps: number | null
  maxTokens: number | null
  abc: string | null
  fullSteps: number | null
  abcSampling: SamplingOverrides | null
  semanticSampling: SamplingOverrides | null
}

export interface RunInfo {
  /** The run folder's name; stays when the run is renamed. */
  id: string
  /** The title given in this app if there is one, else the worker's. */
  title: string
  /** The worker's title, which the folder is named after; an emptied title returns to it. */
  originalTitle: string
  createdAt: string | null
  style: string
  lyrics: string
  songs: SongInfo[]
  /** What the whole run folder takes on disk. */
  bytes: number
}

/** The playlist (PlaylistEndpoints.cs): song ids (`run/songN`) in the order they play. */
export interface PlaylistInfo {
  songIds: string[]
}

/** What a lyrics draft is written in (Lyrics/LyricsLanguage.cs). */
export type LyricsLanguage = 'english' | 'german'

/** A language model LM Studio offers for drafts (Lyrics/LyricsModels.cs). */
export interface LyricsModel {
  id: string
  name: string
  /** Size on disk, roughly what it takes in memory; null when the server does not say. */
  sizeBytes: number | null
  /** Already loaded in LM Studio, so a draft uses it without loading. */
  loaded: boolean
}

/** What the lyrics model picker offers; `default` is the configured model, used when a draft names none. */
export interface LyricsModels {
  default: string
  models: LyricsModel[]
}

/** The last lyrics draft from the local language model (Worker/WorkerModels.cs); it arrives as `lyrics` events. */
export interface LyricsState {
  id: string
  stage: 'writing' | 'done' | 'failed'
  lyrics: string | null
  /** Why it failed, e.g. LM Studio's own message. */
  message: string | null
  updatedAt: string
  finished: boolean
}

/** The volume the songs are written to (LibraryEndpoints.cs). */
/** Whether a yue-to-logic-pro server is configured, i.e. whether songs can become Logic projects. */
export interface LogicExportInfo {
  configured: boolean
}

/** A warning yue-to-logic-pro gave with a Logic project (its own record, PascalCase severity). */
export interface LogicDiagnostic {
  severity: 'Info' | 'Warning' | 'Error'
  code: string
  message: string
}

export interface StorageInfo {
  freeBytes: number
  totalBytes: number
}
