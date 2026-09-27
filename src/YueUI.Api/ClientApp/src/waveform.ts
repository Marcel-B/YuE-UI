/**
 * The loudness outline of a recording, decoded in the browser: for each of `buckets` equal slices the highest
 * absolute sample across all channels, scaled so the loudest slice is 1. Null when the browser cannot decode the
 * format (the same files it cannot play), so the caller simply shows no waveform.
 */
export async function peaksOf(file: Blob, buckets: number): Promise<number[] | null> {
  let context: AudioContext | null = null
  try {
    context = new AudioContext()
    const audio = await context.decodeAudioData(await file.arrayBuffer())
    const channels = Array.from({ length: audio.numberOfChannels }, (_, index) => audio.getChannelData(index))
    const size = Math.max(1, Math.floor(audio.length / buckets))
    const peaks = new Array<number>(buckets).fill(0)
    for (const samples of channels) {
      for (let bucket = 0; bucket < buckets; bucket++) {
        const end = Math.min(samples.length, (bucket + 1) * size)
        let peak = peaks[bucket]!
        for (let index = bucket * size; index < end; index++) {
          const value = Math.abs(samples[index]!)
          if (value > peak) {
            peak = value
          }
        }
        peaks[bucket] = peak
      }
    }
    const loudest = Math.max(...peaks)
    return loudest > 0 ? peaks.map((peak) => peak / loudest) : peaks
  } catch {
    return null
  } finally {
    // Browsers allow only a few open contexts; this one was needed only for decoding.
    void context?.close().catch(() => {})
  }
}
