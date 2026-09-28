import { ref } from 'vue'

declare const __BUILD_ID__: string

/**
 * Whether the server ships a newer build than the one running. The home screen app on iOS has no reload button and
 * resumes from memory instead of loading the page again, so without this it keeps the old build until it is closed.
 */
export const updateAvailable = ref(false)

/** Asks for the deployed build's id (vite.config.ts, `buildVersion`); a failed check just waits for the next one. */
export async function checkForUpdate(): Promise<void> {
  if (__BUILD_ID__ === 'dev' || updateAvailable.value) return
  try {
    const response = await fetch(`${import.meta.env.BASE_URL}version.json`, { cache: 'no-store' })
    if (!response.ok) return
    const { build } = (await response.json()) as { build?: string }
    updateAvailable.value = !!build && build !== __BUILD_ID__
  } catch {
    // Offline or the server restarting; the next snapshot or visit checks again.
  }
}

/** Standalone, i.e. opened from the home screen: there is no browser reload button, so the app offers its own. */
export const standalone =
  window.matchMedia('(display-mode: standalone)').matches ||
  (navigator as Navigator & { standalone?: boolean }).standalone === true

// Coming back to the app is when iOS would have shown the old build.
document.addEventListener('visibilitychange', () => {
  if (document.visibilityState === 'visible') void checkForUpdate()
})

export function reload(): void {
  window.location.reload()
}
