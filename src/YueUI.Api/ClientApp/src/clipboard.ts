/**
 * Puts a text on the clipboard. `navigator.clipboard` exists only in a secure context (Tailscale's HTTPS,
 * localhost); over plain HTTP the old `execCommand('copy')` on a hidden textarea still works in Safari and Chromium.
 */
export async function copyText(text: string): Promise<boolean> {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text)
      return true
    } catch {
      // Fall through: some browsers refuse when the page lost focus, the old way may still work.
    }
  }
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.opacity = '0'
  document.body.appendChild(area)
  area.select()
  try {
    return document.execCommand('copy')
  } catch {
    return false
  } finally {
    area.remove()
  }
}

/**
 * Puts a text on the clipboard that is still being fetched. Safari allows writing only during the tap, which an awaited
 * request outlasts; a `ClipboardItem` given the pending text is written within it and filled when the text arrives.
 * Elsewhere, or where that fails, the text is copied once it is there.
 */
export async function copyPending(text: Promise<string>): Promise<boolean> {
  if (typeof ClipboardItem !== 'undefined' && navigator.clipboard?.write) {
    try {
      const blob = text.then((value) => new Blob([value], { type: 'text/plain' }))
      await navigator.clipboard.write([new ClipboardItem({ 'text/plain': blob })])
      return true
    } catch {
      // A browser that wants the text itself, not a promise of it: try again with the text below.
    }
  }
  return copyText(await text)
}
