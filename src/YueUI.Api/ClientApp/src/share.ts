import { ref } from 'vue'
import { saveBlob, songShareFile } from './api'

/**
 * Sharing a song from the phone: the server makes a small AAC, and the system's share sheet (Web Share API) sends
 * it on, to a messenger for instance. `ShareDialog.vue` shows the state; any button only calls `shareSong`.
 */
export type ShareState =
  | { step: 'preparing'; songId: string }
  /** The file is there, but the share sheet needs a fresh tap (see `shareSong`). */
  | { step: 'ready'; file: File }
  | { step: 'failed'; message: string }

export const shareState = ref<ShareState | null>(null)

export async function shareSong(songId: string): Promise<void> {
  if (shareState.value?.step === 'preparing') {
    return
  }
  shareState.value = { step: 'preparing', songId }
  let file: File
  try {
    file = await songShareFile(songId)
  } catch (caught) {
    shareState.value = { step: 'failed', message: caught instanceof Error ? caught.message : String(caught) }
    return
  }
  // Only the latest request counts, should the dialog have been closed and another song shared meanwhile.
  if (shareState.value?.step !== 'preparing' || shareState.value.songId !== songId) {
    return
  }
  if (!canShare(file)) {
    // Desktop browsers without a share sheet for files: the small file is still worth having.
    saveBlob(file, file.name)
    shareState.value = null
    return
  }
  // Safari opens the share sheet only within a few seconds of a tap, which encoding may exceed. Try it; if the
  // tap has expired, the dialog offers a button that is a fresh tap.
  shareState.value = { step: 'ready', file }
  await shareNow()
}

/** Opens the share sheet for the prepared file; called right after preparing and by the dialog's button. */
export async function shareNow(): Promise<void> {
  const state = shareState.value
  if (state?.step !== 'ready') {
    return
  }
  try {
    await navigator.share({ files: [state.file], title: state.file.name.replace(/\.m4a$/, '') })
    shareState.value = null
  } catch (caught) {
    if (caught instanceof DOMException && caught.name === 'NotAllowedError') {
      return // stays ready: the dialog's button is the fresh tap
    }
    if (caught instanceof DOMException && caught.name === 'AbortError') {
      shareState.value = null // closed the share sheet
      return
    }
    shareState.value = { step: 'failed', message: caught instanceof Error ? caught.message : String(caught) }
  }
}

function canShare(file: File): boolean {
  return typeof navigator.canShare === 'function' && navigator.canShare({ files: [file] })
}
