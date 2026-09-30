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
