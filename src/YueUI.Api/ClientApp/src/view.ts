import { ref } from 'vue'

/** The pages the menu bar switches between. */
export type View = 'create' | 'transcribe' | 'songs' | 'playlist'

const views: readonly View[] = ['create', 'transcribe', 'songs', 'playlist']

/**
 * The page lives in the URL's hash (`#/songs`), so the back button, a reload and a bookmark on the home screen keep it,
 * and the server needs no route per page. Only the page's content switches; the player sits outside of it.
 */
function fromHash(): View {
  const name = location.hash.replace(/^#\/?/, '')
  return (views as readonly string[]).includes(name) ? (name as View) : 'create'
}

export const view = ref<View>(fromHash())

window.addEventListener('hashchange', () => {
  view.value = fromHash()
})

export function navigate(target: View): void {
  if (view.value !== target) {
    location.hash = `#/${target}`
    view.value = target
  }
  window.scrollTo({ top: 0 })
}
