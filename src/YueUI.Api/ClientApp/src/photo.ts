/** Longest side a photo is sent with: enough for a vision model (Gemma 4 sees at most about this), some 200 KB. */
const maxSide = 1024

/**
 * A photo as a JPEG data URL for a lyrics draft, scaled down in the browser: a phone's original is several MB and
 * often HEIC, which the language model cannot read. Drawing it through an `<img>` also applies its EXIF rotation.
 */
export async function photoDataUrl(file: File): Promise<string> {
  const url = URL.createObjectURL(file)
  try {
    const image = new Image()
    image.src = url
    await image.decode()
    const scale = Math.min(1, maxSide / Math.max(image.naturalWidth, image.naturalHeight))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(image.naturalWidth * scale))
    canvas.height = Math.max(1, Math.round(image.naturalHeight * scale))
    const context = canvas.getContext('2d')
    if (!context) {
      throw new Error('No canvas to scale the photo.')
    }
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
    return canvas.toDataURL('image/jpeg', 0.85)
  } finally {
    URL.revokeObjectURL(url)
  }
}
