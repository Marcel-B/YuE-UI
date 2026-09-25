import { computed, ref } from 'vue'
import { audioUrl } from './api'
import { t } from './i18n'
import type { RunInfo, SongInfo } from './types'

/** A song as the player shows it. */
export interface Track {
  id: string
  title: string
  /** "Song 2", in the language of the moment it was queued. */
  detail: string
}

export function trackOf(run: RunInfo, song: SongInfo): Track {
  return { id: song.id, title: run.title || t('untitled'), detail: t('songN', { n: song.index }) }
}

/** Every song with audio, in the library's order: what plays on after a song started from the library. */
export function libraryTracks(runs: RunInfo[]): Track[] {
  return runs.flatMap((run) => run.songs.filter((song) => song.hasAudio).map((song) => trackOf(run, song)))
}

/**
 * A run renamed since its songs were queued: the player and the lock screen take the new title, without
 * interrupting the song.
 */
export function retitle(runs: RunInfo[]): void {
  const titles = new Map(runs.flatMap((run) => run.songs.map((song) => [song.id, run.title || t('untitled')] as const)))
  if (!tracks.value.some((track) => titles.has(track.id) && titles.get(track.id) !== track.title)) {
    return
  }
  tracks.value = tracks.value.map((track) => ({ ...track, title: titles.get(track.id) ?? track.title }))
  if (current.value) {
    showOnLockScreen(current.value)
  }
}

/**
 * The one player of the page. It lives outside the pages (PlayerBar.vue in App.vue), so switching between them does
 * not stop the song; the list it was started from decides what plays next.
 */
const tracks = ref<Track[]>([])
const position = ref(-1)
/** Mirrors the audio element, for the play/pause icons next to the songs. */
export const playing = ref(false)

export const current = computed<Track | null>(() => tracks.value[position.value] ?? null)
export const hasNext = computed(() => position.value >= 0 && position.value < tracks.value.length - 1)
export const hasPrevious = computed(() => position.value > 0)

let audio: HTMLAudioElement | null = null

/** PlayerBar hands over its audio element; play() then starts it right in the click, which iOS insists on. */
export function attach(element: HTMLAudioElement | null): void {
  audio = element
}

/** Plays the track, and the tracks after it in `list` when it ends. Its own track again toggles pause. */
export function play(track: Track, list: Track[] = [track]): void {
  if (current.value?.id === track.id && audio) {
    toggle()
    return
  }
  const index = list.findIndex((item) => item.id === track.id)
  tracks.value = index >= 0 ? list : [track]
  position.value = Math.max(index, 0)
  start()
}

export function toggle(): void {
  if (!audio || !current.value) {
    return
  }
  if (audio.paused) {
    void audio.play().catch(() => undefined)
  } else {
    audio.pause()
  }
}

/** @returns False at the end of the list. */
export function next(): boolean {
  if (!hasNext.value) {
    return false
  }
  position.value++
  start()
  return true
}

export function previous(): void {
  if (audio && audio.currentTime > 3) {
    // As every player does: back to the start of the song first, one more press for the one before.
    audio.currentTime = 0
  } else if (hasPrevious.value) {
    position.value--
    start()
  }
}

export function close(): void {
  audio?.pause()
  audio?.removeAttribute('src')
  audio?.load()
  tracks.value = []
  position.value = -1
}

function start(): void {
  const track = current.value
  if (!audio || !track) {
    return
  }
  audio.src = audioUrl(track.id)
  // A refusal (autoplay rules, a deleted file) leaves the player paused with its own controls to try again.
  void audio.play().catch(() => undefined)
  showOnLockScreen(track)
}

/** The title on the lock screen and in Control Center, with skip buttons that follow the list. */
function showOnLockScreen(track: Track): void {
  if (!('mediaSession' in navigator)) {
    return
  }
  navigator.mediaSession.metadata = new MediaMetadata({ title: track.title, artist: track.detail, album: 'YuE UI' })
  navigator.mediaSession.setActionHandler('nexttrack', hasNext.value ? () => void next() : null)
  navigator.mediaSession.setActionHandler('previoustrack', () => previous())
}
