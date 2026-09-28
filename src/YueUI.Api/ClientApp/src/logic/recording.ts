/**
 * The song's own recording (its audio.flac) in the preview: decoded once, drawn as a waveform above the tracks and
 * played along with the notes, so one can hear and see whether score and recording belong together before exporting.
 */

/** Resolution of the waveform: one peak per 5 ms, fine enough for the closest zoom of the piano roll. */
const SECONDS_PER_PEAK = 0.005

export interface Recording {
  buffer: AudioBuffer
  /** The loudest sample of each slice across all channels, scaled so the loudest slice of the song is 1. */
  peaks: Float32Array
  secondsPerPeak: number
}

/**
 * Decodes the recording. Null when the browser cannot decode it (the same files it cannot play), so the caller shows
 * a note instead of the waveform. The context only decodes here; playback happens on the player's.
 */
export async function decodeRecording(file: Blob, context: BaseAudioContext): Promise<Recording | null> {
  let buffer: AudioBuffer
  try {
    buffer = await context.decodeAudioData(await file.arrayBuffer())
  } catch {
    return null
  }
  const size = Math.max(1, Math.round(buffer.sampleRate * SECONDS_PER_PEAK))
  const peaks = new Float32Array(Math.ceil(buffer.length / size))
  for (let channel = 0; channel < buffer.numberOfChannels; channel++) {
    const samples = buffer.getChannelData(channel)
    for (let index = 0; index < samples.length; index++) {
      const value = Math.abs(samples[index]!)
      const slot = (index / size) | 0
      if (value > peaks[slot]!) {
        peaks[slot] = value
      }
    }
  }
  let loudest = 0
  for (const peak of peaks) {
    loudest = Math.max(loudest, peak)
  }
  if (loudest > 0) {
    for (let index = 0; index < peaks.length; index++) {
      peaks[index]! /= loudest
    }
  }
  return { buffer, peaks, secondsPerPeak: size / buffer.sampleRate }
}

/** Plays the recording from a position; one source per start, since an AudioBufferSourceNode plays only once. */
export class RecordingPlayer {
  private readonly gain: GainNode
  private source: AudioBufferSourceNode | null = null

  constructor(
    readonly context: AudioContext,
    private readonly buffer: AudioBuffer,
    volume: number,
  ) {
    this.gain = context.createGain()
    this.gain.gain.value = volume
    this.gain.connect(context.destination)
  }

  /**
   * Starts at `seconds` into the recording; a negative position (the playhead in a count-in) starts it that much
   * later from its beginning. Must run inside the click that started playback, or iOS keeps the context silent.
   */
  start(seconds: number): void {
    this.stop()
    void this.context.resume()
    if (seconds >= this.buffer.duration) {
      return
    }
    const source = this.context.createBufferSource()
    source.buffer = this.buffer
    source.connect(this.gain)
    const now = this.context.currentTime
    source.start(now + Math.max(0, -seconds), Math.max(0, seconds))
    this.source = source
  }

  stop(): void {
    if (this.source) {
      try {
        this.source.stop()
      } catch {
        // Already ended on its own.
      }
      this.source.disconnect()
      this.source = null
    }
  }

  setVolume(volume: number): void {
    this.gain.gain.setTargetAtTime(volume, this.context.currentTime, 0.02)
  }

  close(): void {
    this.stop()
    this.gain.disconnect()
  }
}
