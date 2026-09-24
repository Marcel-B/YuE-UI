import type {
  GenerateRequest,
  LogEntry,
  LyricsState,
  RunInfo,
  SongState,
  StatusSnapshot,
  StorageInfo,
  TranscriptionList,
  TranscriptionState,
  TranscriptionTask,
  WorkerInfo,
} from './types'

const apiBase = import.meta.env.VITE_API_BASE ?? ''

/** A request the backend refused or could not answer; `status` is 0 for network errors. */
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
    /** Field errors of a validation problem, keyed by the request's property names. */
    readonly errors: Record<string, string[]> = {},
  ) {
    super(message)
  }
}

/** Queues a run; its songs then arrive as `song` events. */
export async function generate(request: GenerateRequest): Promise<void> {
  await send('/api/generate', json('POST', request))
}

/**
 * Starts English lyrics in YuE2's format from a few keywords, written by the language model in LM Studio; the draft
 * arrives as a `lyrics` event. Refused (409) while YuE2 generates or another draft is being written.
 */
export async function draftLyrics(keywords: string, style: string): Promise<LyricsState> {
  return (await send('/api/lyrics', json('POST', { keywords, style }))).json() as Promise<LyricsState>
}

/** Synthesizes a finished song again from its saved tokens, normally a draft at full quality. */
export async function render(songId: string, quality: 'full' | 'draft' = 'full'): Promise<void> {
  await send(`/api/songs/${songId}/render`, json('POST', { quality }))
}

export async function cancel(songId: string): Promise<void> {
  await send(`/api/songs/${songId}/cancel`, { method: 'POST' })
}

export async function stopAll(): Promise<void> {
  await send('/api/stop', { method: 'POST' })
}

/** Ends the worker process, which frees the memory the model holds. */
export async function shutdownWorker(): Promise<void> {
  await send('/api/worker/shutdown', { method: 'POST' })
}

export async function listLibrary(): Promise<RunInfo[]> {
  return (await send('/api/library')).json() as Promise<RunInfo[]>
}

/** Deletes the song's folder, and the run's with the last song; refused while the worker is on it. */
export async function deleteSong(songId: string): Promise<void> {
  await send(`/api/songs/${songId}`, { method: 'DELETE' })
}

/** Deletes the run's folder with all its songs; refused while the worker is on one of them. */
export async function deleteRun(runId: string): Promise<void> {
  await send(`/api/runs/${runId}`, { method: 'DELETE' })
}

/** Free and total space of the volume the songs are written to. */
export async function getStorage(): Promise<StorageInfo> {
  return (await send('/api/storage')).json() as Promise<StorageInfo>
}

export function audioUrl(songId: string, download = false): string {
  return `${apiBase}/api/songs/${songId}/audio${download ? '?download=true' : ''}`
}

export function scoreUrl(songId: string): string {
  return `${apiBase}/api/songs/${songId}/score`
}

/** The song's score.abc as text, to show it or to use it for the next song. */
export async function songScore(songId: string): Promise<string> {
  return (await send(`/api/songs/${songId}/score`)).text()
}

/** The song's audio.flac and score.abc in one archive. */
export function songZipUrl(songId: string): string {
  return `${apiBase}/api/songs/${songId}/zip`
}

/** Audio and score of every song of the run. */
export function runZipUrl(runId: string): string {
  return `${apiBase}/api/runs/${runId}/zip`
}

/** Uploads a recording for SheetSage2; its progress then arrives as `transcription` events. */
export async function transcribe(file: File, task: TranscriptionTask): Promise<TranscriptionState> {
  const form = new FormData()
  form.append('file', file)
  form.append('task', task)
  return (await send('/api/transcriptions', { method: 'POST', body: form })).json() as Promise<TranscriptionState>
}

export async function cancelTranscription(id: string): Promise<void> {
  await send(`/api/transcriptions/${id}/cancel`, { method: 'POST' })
}

export async function listTranscriptions(): Promise<TranscriptionList> {
  return (await send('/api/transcriptions')).json() as Promise<TranscriptionList>
}

export async function transcriptionScore(id: string): Promise<string> {
  return (await send(`/api/transcriptions/${encodeURIComponent(id)}/score`)).text()
}

export async function deleteTranscription(id: string): Promise<void> {
  await send(`/api/transcriptions/${encodeURIComponent(id)}`, { method: 'DELETE' })
}

export function transcriptionScoreUrl(id: string): string {
  return `${apiBase}/api/transcriptions/${encodeURIComponent(id)}/score?download=true`
}

/** Score, MIDI parts and annotations of a transcription. */
export function transcriptionZipUrl(id: string): string {
  return `${apiBase}/api/transcriptions/${encodeURIComponent(id)}/zip`
}

export interface EventHandlers {
  snapshot(snapshot: StatusSnapshot): void
  song(song: SongState): void
  worker(worker: WorkerInfo): void
  log(entry: LogEntry): void
  /** A song was written to disk or deleted: the library has changed. */
  library(): void
  transcription(transcription: TranscriptionState): void
  lyrics(lyrics: LyricsState): void
  /** False while the stream is down; the browser reconnects by itself and a new snapshot follows. */
  connection(open: boolean): void
}

/** Follows the worker live. Returns a function that closes the stream. */
export function subscribe(handlers: EventHandlers): () => void {
  const source = new EventSource(`${apiBase}/api/events`)
  const on = <T>(type: string, handle: (data: T) => void) =>
    source.addEventListener(type, (event) => handle(JSON.parse((event as MessageEvent<string>).data) as T))

  on<StatusSnapshot>('snapshot', (snapshot) => {
    handlers.connection(true)
    handlers.snapshot(snapshot)
  })
  on<SongState>('song', handlers.song)
  on<WorkerInfo>('worker', handlers.worker)
  on<LogEntry>('log', handlers.log)
  on('library', () => handlers.library())
  on<TranscriptionState>('transcription', handlers.transcription)
  on<LyricsState>('lyrics', handlers.lyrics)
  source.onerror = () => handlers.connection(false)
  return () => source.close()
}

function json(method: string, body: unknown): RequestInit {
  return { method, body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } }
}

/** A request whose failure is worth an ApiError: a problem document's detail, or the status. */
async function send(path: string, init: RequestInit = {}): Promise<Response> {
  let response: Response
  try {
    response = await fetch(`${apiBase}${path}`, init)
  } catch {
    throw new ApiError('network', 0)
  }

  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as {
      title?: string
      detail?: string
      errors?: Record<string, string[]>
    } | null
    throw new ApiError(
      problem?.detail ?? problem?.title ?? `HTTP ${response.status}`,
      response.status,
      problem?.errors ?? {},
    )
  }
  return response
}
