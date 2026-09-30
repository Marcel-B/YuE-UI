/**
 * What both browser synthesizers share: the envelope, how it is scheduled, and the helpers that make stored JSON
 * playable. Its own module so the analog and the FM engine can both use it without importing each other.
 */

export const WAVES = ['sine', 'triangle', 'sawtooth', 'square'] as const
export type Wave = (typeof WAVES)[number]

/** Times in seconds, sustain as a share of the peak. */
export interface Envelope {
  attack: number
  decay: number
  sustain: number
  release: number
}

export const ENVELOPE_RANGES = {
  attack: [0, 4],
  decay: [0.01, 4],
  sustain: [0, 1],
  release: [0.01, 6],
} as const satisfies Record<string, readonly [number, number]>

/**
 * Schedules an envelope on `param` from `start` to the note's end and its release. The note's length is known in
 * advance, so the level at the moment the key is let go is computed rather than read back: Web Audio cannot report
 * a parameter's scheduled value, and `cancelAndHoldAtTime`, which would, is missing in Firefox. Linear attack, then
 * an exponential fall to the sustain level; `decay` is the time to get about 98 % of the way there.
 *
 * @returns When the release has died away.
 */
export function envelope(
  param: AudioParam,
  env: Envelope,
  start: number,
  end: number,
  base: number,
  peak: number,
): number {
  const attack = Math.max(0.002, env.attack)
  const tau = Math.max(0.002, env.decay) / 4
  const at = (t: number) => (t < attack ? t / attack : env.sustain + (1 - env.sustain) * Math.exp(-(t - attack) / tau))
  const held = end - start
  param.setValueAtTime(base, start)
  if (held <= attack) {
    param.linearRampToValueAtTime(base + peak * at(held), end)
  } else {
    param.linearRampToValueAtTime(base + peak, start + attack)
    param.setTargetAtTime(base + peak * env.sustain, start + attack, tau)
    param.setValueAtTime(base + peak * at(held), end)
  }
  const release = Math.max(0.005, env.release)
  param.setTargetAtTime(base, end, release / 4)
  return end + release
}

export function clamp(value: unknown, [min, max]: readonly [number, number], fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : fallback
}

export function oneOf<T extends string>(value: unknown, options: readonly T[], fallback: T): T {
  return options.includes(value as T) ? (value as T) : fallback
}

export function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

export function envelopeOf(raw: unknown, fallback: Envelope): Envelope {
  const value = record(raw)
  return {
    attack: clamp(value.attack, ENVELOPE_RANGES.attack, fallback.attack),
    decay: clamp(value.decay, ENVELOPE_RANGES.decay, fallback.decay),
    sustain: clamp(value.sustain, ENVELOPE_RANGES.sustain, fallback.sustain),
    release: clamp(value.release, ENVELOPE_RANGES.release, fallback.release),
  }
}
