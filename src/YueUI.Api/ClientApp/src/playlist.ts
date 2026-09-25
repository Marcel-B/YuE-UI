import { ref } from 'vue'
import { getPlaylist, putPlaylist } from './api'

/**
 * The playlist's song ids in order. The server keeps it, so the phone and the Mac see the same one; this is the copy
 * the page shows, updated right away and put back if the server refuses.
 */
export const playlistIds = ref<string[]>([])

export async function loadPlaylist(): Promise<void> {
  playlistIds.value = (await getPlaylist()).songIds
}

export async function savePlaylist(songIds: string[]): Promise<void> {
  const before = playlistIds.value
  playlistIds.value = songIds
  try {
    playlistIds.value = (await putPlaylist(songIds)).songIds
  } catch (caught) {
    playlistIds.value = before
    throw caught
  }
}

export function toggleInPlaylist(songId: string): Promise<void> {
  return savePlaylist(
    playlistIds.value.includes(songId)
      ? playlistIds.value.filter((id) => id !== songId)
      : [...playlistIds.value, songId],
  )
}
