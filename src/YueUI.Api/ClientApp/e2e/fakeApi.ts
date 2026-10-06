import { test as base, type Page, type Route } from '@playwright/test'
import type { PlaylistInfo, QueuedJob, RunInfo, SongInfo, StatusSnapshot } from '../src/types'

/** A request the page made, with its JSON body if it had one. */
export interface Call {
  method: string
  path: string
  body: unknown
}

/**
 * The server as the page sees it: /api answered from this state, and the event stream replaced by one the test
 * drives, since a fulfilled route can only send a finished body and EventSource would reconnect after it.
 */
export class FakeApi {
  runs: RunInfo[] = []
  playlists: PlaylistInfo[] = [{ id: 1, name: 'Playlist', songIds: [] }]
  snapshot: StatusSnapshot = {
    worker: { status: 'ready', busy: false, studioRunning: false, lastError: null, extensions: true },
    songs: [],
    log: [],
    transcriptions: [],
    lyrics: null,
    versions: [],
    queue: [],
    bundleWindowSeconds: 1200,
    stems: [],
    speech: [],
    swaps: [],
    videos: [],
    images: [],
    memory: { holder: null },
  }
  readonly calls: Call[] = []
  /** Answers of a test's own, by "METHOD /path", before the defaults. */
  readonly answers = new Map<string, (call: Call) => unknown>()

  constructor(private readonly page: Page) {}

  async install(): Promise<void> {
    await this.page.addInitScript(() => {
      class FakeEventSource extends EventTarget {
        onerror: ((event: Event) => void) | null = null
        readyState = 1
        constructor(readonly url: string) {
          super()
          const sources = ((window as unknown as { __sources?: FakeEventSource[] }).__sources ??= [])
          sources.push(this)
          // The server opens every stream with a snapshot.
          void fetch('/api/__snapshot')
            .then((response) => response.text())
            .then((data) => this.dispatchEvent(new MessageEvent('snapshot', { data })))
        }
        close() {
          this.readyState = 2
        }
      }
      window.EventSource = FakeEventSource as unknown as typeof EventSource
    })
    await this.page.route('**/api/**', (route) => this.answer(route))
  }

  /** Sends an event down every open stream, as StatusHub would. */
  async emit(type: string, data: unknown): Promise<void> {
    await this.page.evaluate(
      ([type, data]) => {
        for (const source of (window as unknown as { __sources: EventTarget[] }).__sources) {
          source.dispatchEvent(new MessageEvent(type as string, { data: JSON.stringify(data) }))
        }
      },
      [type, data] as const,
    )
  }

  /** The calls to one route, in order. */
  callsTo(method: string, path: string): Call[] {
    return this.calls.filter((call) => call.method === method && call.path === path)
  }

  private async answer(route: Route): Promise<void> {
    const request = route.request()
    const path = new URL(request.url()).pathname.replace(/^\/ui/, '')
    if (path === '/api/__snapshot') {
      return route.fulfill({ json: this.snapshot })
    }
    let body: unknown = null
    try {
      body = request.postDataJSON()
    } catch {
      body = request.postData()
    }
    const call = { method: request.method(), path, body }
    this.calls.push(call)
    const own = this.answers.get(`${call.method} ${path}`)
    const answer = own ? own(call) : this.default(call)
    if (answer === undefined) {
      return route.fulfill({ status: 404, json: { title: `No fake for ${call.method} ${path}` } })
    }
    return route.fulfill(typeof answer === 'number' ? { status: answer } : { json: answer })
  }

  private default({ method, path, body }: Call): unknown {
    const get = method === 'GET'
    switch (path) {
      case '/api/library':
        return this.runs
      case '/api/playlists':
        return this.playlists
      case '/api/voice':
        return { voicesConfigured: false, conversionConfigured: false, stemsConfigured: false }
      case '/api/transcriptions':
        return { installed: false, items: [] }
      case '/api/templates':
        return []
      case '/api/lyrics/models':
        return { default: 'gemma', models: [] }
      case '/api/loras':
        return { folder: '/loras', loras: [] }
      case '/api/audio-midi':
        return { installed: false, python: '' }
      case '/api/storage':
        return { freeBytes: 100e9, totalBytes: 500e9 }
      case '/api/generate':
        return method === 'POST' ? 202 : undefined
    }
    const rating = path.match(/^\/api\/songs\/(.+)\/rating$/)
    if (rating && method === 'PUT') {
      const song = this.song(rating[1]!)
      if (song) {
        song.rating = (body as { rating: number | null }).rating
      }
      return 204
    }
    return get ? undefined : 204
  }

  private song(id: string): SongInfo | undefined {
    return this.runs.flatMap((run) => run.songs).find((song) => song.id === id)
  }
}

/** A song with nothing set but what a test names. */
export function song(id: string, fields: Partial<SongInfo> = {}): SongInfo {
  return {
    id,
    index: Number(id.match(/song(\d+)$/)?.[1] ?? 1),
    seed: null,
    quality: 'full',
    seconds: 120,
    hasAudio: true,
    hasScore: false,
    canRender: false,
    bytes: 1e6,
    rating: null,
    versions: [],
    coverUpdatedAt: null,
    note: null,
    videoFormats: [],
    ...fields,
  }
}

/** A run with nothing set but what a test names. */
export function run(id: string, fields: Partial<RunInfo> = {}): RunInfo {
  return {
    id,
    title: id,
    originalTitle: id,
    createdAt: '2026-10-01T10:00:00Z',
    style: '',
    lyrics: '',
    songs: [song(`${id}/song1`)],
    bytes: 1e6,
    ...fields,
  }
}

export function queued(id: string, fields: Partial<QueuedJob> = {}): QueuedJob {
  return {
    id,
    kind: 'song',
    title: id,
    createdAt: new Date().toISOString(),
    songId: null,
    batch: 1,
    quality: 'draft',
    revision: false,
    voiceLabel: null,
    ...fields,
  }
}

export const test = base.extend<{ api: FakeApi }>({
  // Automatic, so a test that never looks at the server still never reaches a real one.
  api: [
    async ({ page }, use) => {
      const api = new FakeApi(page)
      await api.install()
      await use(api)
    },
    { auto: true },
  ],
})

export { expect } from '@playwright/test'
