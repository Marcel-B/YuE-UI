import { computed, ref } from 'vue'
import { coverUrl, streamUrl, versionStreamUrl } from './api'
import { t } from './i18n'
import { createSpectrumAnalyser, registerSource, visuals, type SpectrumSource } from './spectrum'
import type { RunInfo, SongInfo, VersionState } from './types'

/** A song as the player shows it. */
export interface Track {
  /** The song's id, or for a version `run/songN@versionId`, so that both can be in one list. */
  id: string
  /** The song the track is, or is a version of: what rating, playlist and the song's address go by. */
  songId: string
  title: string
  /** "Song 2", in the language of the moment it was queued; a version adds its voice. */
  detail: string
  /** Where a version's audio is; a song's own comes from its id. */
  src?: string
  /** The song's own cover, for the player and the lock screen; none while it has only the drawn one. */
  cover?: string
}

function coverOf(song: SongInfo): string | undefined {
  return song.coverUpdatedAt ? coverUrl(song.id, song.coverUpdatedAt) : undefined
}

export function trackOf(run: RunInfo, song: SongInfo): Track {
  return {
    id: song.id,
    songId: song.id,
    title: run.title || t('untitled'),
    detail: t('songN', { n: song.index }),
    cover: coverOf(song),
  }
}

/** The song sung with another voice. */
export function versionTrack(run: RunInfo, song: SongInfo, version: VersionState): Track {
  return {
    id: `${song.id}@${version.id}`,
    songId: song.id,
    title: run.title || t('untitled'),
    detail: `${t('songN', { n: song.index })} · ${version.voiceLabel}`,
    src: versionStreamUrl(song.id, version.id),
    cover: coverOf(song),
  }
}

/** Every song with audio, in the library's order: what plays on after a song started from the library. */
export function libraryTracks(runs: RunInfo[]): Track[] {
  return runs.flatMap((run) => run.songs.filter((song) => song.hasAudio).map((song) => trackOf(run, song)))
}

/**
 * A run renamed or a cover changed since its songs were queued: the player and the lock screen take the new title and
 * cover, without interrupting the song.
 */
export function refreshTracks(runs: RunInfo[]): void {
  const songs = new Map(
    runs.flatMap((run) =>
      run.songs.map((song) => [song.id, { title: run.title || t('untitled'), cover: coverOf(song) }] as const),
    ),
  )
  const changed = (track: Track): boolean => {
    const song = songs.get(track.songId)
    return song !== undefined && (song.title !== track.title || song.cover !== track.cover)
  }
  if (!tracks.value.some(changed)) {
    return
  }
  tracks.value = tracks.value.map((track) => (changed(track) ? { ...track, ...songs.get(track.songId)! } : track))
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

/** Where the song is and how long it runs, in seconds, for the full-screen player's progress bar. */
export const elapsed = ref(0)
export const duration = ref(0)
/** The full-screen player (NowPlaying.vue) is open. */
export const expanded = ref(false)

export const current = computed<Track | null>(() => tracks.value[position.value] ?? null)
export const hasNext = computed(() => position.value >= 0 && position.value < tracks.value.length - 1)
export const hasPrevious = computed(() => position.value > 0)

let audio: HTMLAudioElement | null = null
let context: AudioContext | null = null
let analyser: AnalyserNode | null = null

/** What the player's analyzer and the background listen to; empty until the analyzer first routed the element. */
export const playerSource: SpectrumSource = { analysers: () => (analyser ? [analyser] : []), playing }
registerSource(playerSource)

/**
 * The small analyzer wants the element routed, and so does the open full-screen player when its analyzer or effects
 * are on; a full-screen player never opened leaves a switched-off analyzer as it was.
 */
function wantsAnalyser(): boolean {
  return (
    visuals.value.player || (expanded.value && (visuals.value.nowPlayingAnalyzer || visuals.value.nowPlayingEffects))
  )
}

/**
 * Routes the audio element through Web Audio, for the analyzer. Once routed it stays so (an element cannot leave its
 * source node), so this only happens with the analyzer switched on, and inside a click: iOS starts a context made
 * outside one suspended, and a routed element then plays silence. Called again on every start, since iOS suspends
 * the context while nothing plays. `create: false` only wakes an existing context, for a start outside our buttons (the
 * element's own controls, the lock screen) that may not count as a click.
 */
export function listen(create = true): void {
  if (!audio || (!context && (!create || !wantsAnalyser()))) {
    return
  }
  if (!context) {
    try {
      // "playback" lets Web Audio carry on with the screen locked and the silent switch on, as the bare element does.
      const session = (navigator as Navigator & { audioSession?: { type: string } }).audioSession
      if (session) {
        session.type = 'playback'
      }
      const created = new AudioContext()
      const node = createSpectrumAnalyser(created)
      // The analyser passes its input through unchanged, so it can sit between the element and the speakers.
      created.createMediaElementSource(audio).connect(node).connect(created.destination)
      created.addEventListener('statechange', () => {
        // Interrupted by a call or Siri: the element would go on silently, so the context follows it back.
        if (created.state !== 'running' && audio && !audio.paused) {
          void created.resume().catch(() => undefined)
        }
      })
      context = created
      analyser = node
    } catch {
      return
    }
  }
  if (context.state !== 'running') {
    void context.resume().catch(() => undefined)
  }
}

/**
 * iOS refuses resume() outside a user gesture. The element's own controls and the lock screen start playback through
 * a `play` event that fires after the tap, outside it, so a context iOS suspended while the screen was locked stays
 * suspended and the element plays on into a stopped graph: its clock runs, nothing sounds and the analyzer holds its
 * last frame. Every tap on the page is a gesture, though, and the one on the controls reaches the document before the
 * element starts, so the context wakes on whichever comes first.
 */
function wake(): void {
  if (context && context.state !== 'running') {
    void context.resume().catch(() => undefined)
  }
}
if (typeof document !== 'undefined') {
  for (const type of ['touchend', 'pointerup', 'click', 'keydown']) {
    document.addEventListener(type, wake, { capture: true, passive: true })
  }
}

/**
 * Checks a start that came without a gesture of ours (the lock screen, the element's controls): when the context
 * could not be woken, the song is paused rather than left playing silently with its clock running, so the next tap
 * on play (a gesture) starts it with sound.
 */
export function guardSilence(): void {
  if (!context) {
    return
  }
  setTimeout(() => {
    if (context && context.state !== 'running' && audio && !audio.paused) {
      audio.pause()
    }
  }, 1500)
}

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
    listen()
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

/** Jumps within the playing song. */
export function seek(seconds: number): void {
  if (audio && Number.isFinite(seconds)) {
    audio.currentTime = seconds
    elapsed.value = seconds
  }
}

/** The audio element's clock, as PlayerBar hears it. */
export function updateTime(): void {
  if (audio) {
    elapsed.value = audio.currentTime
    duration.value = Number.isFinite(audio.duration) ? audio.duration : 0
  }
}

export function close(): void {
  expanded.value = false
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
  audio.src = track.src ?? streamUrl(track.id)
  elapsed.value = 0
  duration.value = 0
  listen()
  // A refusal (autoplay rules, a deleted file) leaves the player paused with its own controls to try again.
  void audio.play().catch(() => undefined)
  showOnLockScreen(track)
}

/** The title on the lock screen and in Control Center, with skip buttons that follow the list. */
function showOnLockScreen(track: Track): void {
  if (!('mediaSession' in navigator)) {
    return
  }
  navigator.mediaSession.metadata = new MediaMetadata({
    title: track.title,
    artist: track.detail,
    album: 'Tonwerk',
    // An absolute address: the lock screen fetches it outside the page.
    artwork: track.cover ? [{ src: new URL(track.cover, location.href).href }] : [],
  })
  navigator.mediaSession.setActionHandler('nexttrack', hasNext.value ? () => void next() : null)
  navigator.mediaSession.setActionHandler('previoustrack', () => previous())
  try {
    // The lock screen's progress bar can then be dragged, as the full-screen player's can.
    navigator.mediaSession.setActionHandler('seekto', (details) => seek(details.seekTime ?? 0))
  } catch {
    // Older Safari does not know the action and throws.
  }
}
