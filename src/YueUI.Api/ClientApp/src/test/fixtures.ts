import type { RunInfo, SongInfo } from '../types'

/** A song with nothing set but what a test names. */
export function song(id: string, fields: Partial<SongInfo> = {}): SongInfo {
  return {
    id,
    index: 1,
    seed: null,
    quality: 'draft',
    seconds: null,
    hasAudio: true,
    hasScore: false,
    canRender: false,
    bytes: 0,
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
    title: '',
    originalTitle: '',
    createdAt: null,
    style: '',
    lyrics: '',
    songs: [],
    bytes: 0,
    ...fields,
  }
}
