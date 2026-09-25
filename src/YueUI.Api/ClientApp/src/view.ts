import { ref } from 'vue'

/** The pages the menu bar switches between. */
export type View = 'create' | 'transcribe' | 'songs' | 'playlist'

const views: readonly View[] = ['create', 'transcribe', 'songs', 'playlist']

/**
 * The page lives in the URL's hash (`#/songs`), so the back button, a reload and a bookmark on the home screen keep it,
 * and the server needs no route per page. Only the page's content switches; the player sits outside of it.
 * A song has an address of its own on the songs page, `#/songs/<run>/songN`, which scrolls to it and marks it.
 */
function fromHash(): { view: View; song: string | null } {
  const [name, ...song] = location.hash.replace(/^#\/?/, '').split('/')
  if (!(views as readonly string[]).includes(name ?? '')) {
    return { view: 'create', song: null }
  }
  return { view: name as View, song: name === 'songs' && song.length === 2 ? song.join('/') : null }
}

const initial = fromHash()
export const view = ref<View>(initial.view)
/** The song the address points to, marked on the songs page. */
export const focusedSong = ref<string | null>(initial.song)
/** Counts requests to show a song, so that asking for the one already marked scrolls to it again. */
export const focusRequest = ref(0)

window.addEventListener('hashchange', () => {
  const target = fromHash()
  view.value = target.view
  if (focusedSong.value !== target.song) {
    focusedSong.value = target.song
    focusRequest.value++
  }
})

function setHash(hash: string): void {
  if (location.hash !== hash) {
    location.hash = hash
  }
}

export function navigate(target: View): void {
  setHash(`#/${target}`)
  view.value = target
  focusedSong.value = null
  window.scrollTo({ top: 0 })
}

export function songHref(songId: string): string {
  return `#/songs/${songId}`
}

/** Opens the songs page at the song; the page scrolls there itself once the song is listed. */
export function showSong(songId: string): void {
  setHash(songHref(songId))
  view.value = 'songs'
  focusedSong.value = songId
  focusRequest.value++
}
