import { ref } from 'vue'
import type { ExportTarget } from './export'

/**
 * Covers painted by FLUX.2 Klein on the Mac (Images/ImageMaker.cs). `ImageDialog.vue` (one, in App.vue) asks for the
 * description and lists the song's candidates; the library opens it.
 */
export const imageTarget = ref<ExportTarget | null>(null)

export function openImages(target: ExportTarget): void {
  imageTarget.value = target
}

/**
 * A description for a cover from the song's title and style. In English, since Klein reads it through Qwen3, which
 * follows English best. Without the title in the picture it asks for none: Klein writes text readily, often wrongly.
 */
export function suggestPrompt(title: string, style: string, withTitle: boolean): string {
  const mood = style.trim().replace(/\s+/g, ' ')
  return [
    `Square album cover art for a song${title ? ` called "${title}"` : ''}.`,
    mood ? `It sounds like: ${mood}.` : null,
    'One striking image with a strong central motif, rich colour and dramatic light, painterly, high detail.',
    withTitle && title
      ? `The title "${title}" is written once in elegant, legible lettering.`
      : 'No text, no letters, no logos.',
  ]
    .filter(Boolean)
    .join(' ')
}

export interface ImageSettings {
  model: string | null
  withTitle: boolean
}

const storageKey = 'yue-ui.images'

/** Each browser remembers the model and the title switch. */
export function loadImageSettings(): ImageSettings {
  try {
    const stored = JSON.parse(localStorage.getItem(storageKey) ?? '{}') as Partial<ImageSettings>
    return {
      model: typeof stored.model === 'string' ? stored.model : null,
      withTitle: stored.withTitle === true,
    }
  } catch {
    return { model: null, withTitle: false }
  }
}

export function saveImageSettings(settings: ImageSettings): void {
  try {
    localStorage.setItem(storageKey, JSON.stringify(settings))
  } catch {
    // private mode: only this dialog has them
  }
}
