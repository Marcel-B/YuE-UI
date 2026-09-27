import type { TrackKind } from './types'

/**
 * The browser synthesizer the preview plays pitched tracks on when they are not routed to MIDI hardware: two
 * oscillators and noise into a filter, an envelope each for loudness and cutoff, and an LFO. Only for listening in
 * the browser; the MIDI file and the Logic project know nothing of it.
 *
 * A patch is plain JSON so the server can keep it without reading it; `normalizePatch` makes whatever comes back
 * (an older patch, a hand-edited row) playable, filling what is missing and clamping what is out of range.
 */

export const WAVES = ['sine', 'triangle', 'sawtooth', 'square'] as const
export type Wave = (typeof WAVES)[number]
export const FILTER_TYPES = ['lowpass', 'highpass', 'bandpass'] as const
export type FilterType = (typeof FILTER_TYPES)[number]
export const LFO_TARGETS = ['pitch', 'filter', 'amp'] as const
export type LfoTarget = (typeof LFO_TARGETS)[number]

/** Times in seconds, sustain as a share of the peak. */
export interface Envelope {
  attack: number
  decay: number
  sustain: number
  release: number
}

export interface Oscillator {
  /** `square` is the pulse wave; at a width of 50 % and no PWM it is the plain square. */
  wave: Wave
  /** Whole octaves up or down from the note. */
  octave: number
  /** Cents, for the beating of two slightly detuned oscillators. */
  detune: number
  level: number
  /** The pulse wave's duty cycle, 0.05 to 0.95; only `square` has one. */
  width: number
  /** How far the LFO moves the pulse width, 0 to 1, at the LFO's rate and wave whatever its own target. */
  pwm: number
}

export interface SynthPatch {
  osc1: Oscillator
  osc2: Oscillator
  noise: number
  filter: {
    type: FilterType
    /** Hz at middle C; key tracking moves it with the note. */
    cutoff: number
    /** The filter's Q. */
    resonance: number
    /** How far the filter envelope opens the cutoff, in octaves; negative closes it. */
    envAmount: number
    /** 0: the same cutoff for every note, 1: the cutoff follows the note fully. */
    keyTrack: number
  }
  filterEnv: Envelope
  ampEnv: Envelope
  lfo: {
    wave: Wave
    /** Hz. */
    rate: number
    target: LfoTarget
    /** 0 to 1: up to a semitone of vibrato, two octaves of filter sweep, or full tremolo. */
    depth: number
  }
  volume: number
}

/** Ranges shared by the editor's sliders and `normalizePatch`. */
export const RANGES = {
  octave: [-2, 2],
  detune: [-50, 50],
  level: [0, 1],
  width: [0.05, 0.95],
  pwm: [0, 1],
  cutoff: [20, 18000],
  resonance: [0, 20],
  envAmount: [-4, 6],
  keyTrack: [0, 1],
  attack: [0, 4],
  decay: [0.01, 4],
  sustain: [0, 1],
  release: [0.01, 6],
  rate: [0.1, 20],
  depth: [0, 1],
  volume: [0, 1],
} as const satisfies Record<string, readonly [number, number]>

const LEAD: SynthPatch = {
  osc1: { wave: 'sawtooth', octave: 0, detune: 0, level: 0.7, width: 0.5, pwm: 0 },
  osc2: { wave: 'square', octave: 0, detune: 7, level: 0.35, width: 0.5, pwm: 0 },
  noise: 0,
  filter: { type: 'lowpass', cutoff: 1800, resonance: 2, envAmount: 1.5, keyTrack: 0.5 },
  filterEnv: { attack: 0.01, decay: 0.4, sustain: 0.3, release: 0.2 },
  ampEnv: { attack: 0.01, decay: 0.3, sustain: 0.75, release: 0.15 },
  lfo: { wave: 'sine', rate: 5.5, target: 'pitch', depth: 0.1 },
  volume: 0.8,
}

/** A starting sound per kind of track, so the preview does not play every part on the same buzz. */
const DEFAULTS: Record<Exclude<TrackKind, 'Drums'>, SynthPatch> = {
  Melody: LEAD,
  Doubling: { ...LEAD, volume: 0.5 },
  Chords: {
    osc1: { wave: 'sawtooth', octave: 0, detune: -8, level: 0.5, width: 0.5, pwm: 0 },
    osc2: { wave: 'sawtooth', octave: 0, detune: 8, level: 0.5, width: 0.5, pwm: 0 },
    noise: 0,
    filter: { type: 'lowpass', cutoff: 1400, resonance: 1, envAmount: 1, keyTrack: 0.3 },
    filterEnv: { attack: 0.2, decay: 1, sustain: 0.5, release: 0.6 },
    ampEnv: { attack: 0.08, decay: 0.8, sustain: 0.8, release: 0.5 },
    lfo: { wave: 'triangle', rate: 0.4, target: 'filter', depth: 0.15 },
    volume: 0.45,
  },
  Bass: {
    osc1: { wave: 'sawtooth', octave: 0, detune: 0, level: 0.7, width: 0.5, pwm: 0 },
    osc2: { wave: 'square', octave: -1, detune: 0, level: 0.5, width: 0.5, pwm: 0 },
    noise: 0,
    filter: { type: 'lowpass', cutoff: 380, resonance: 5, envAmount: 2, keyTrack: 0.3 },
    filterEnv: { attack: 0.005, decay: 0.25, sustain: 0.15, release: 0.1 },
    ampEnv: { attack: 0.005, decay: 0.3, sustain: 0.85, release: 0.08 },
    lfo: { wave: 'sine', rate: 5, target: 'pitch', depth: 0 },
    volume: 0.9,
  },
  GuideTones: {
    osc1: { wave: 'sine', octave: 0, detune: 0, level: 0.8, width: 0.5, pwm: 0 },
    osc2: { wave: 'triangle', octave: 1, detune: 0, level: 0.2, width: 0.5, pwm: 0 },
    noise: 0,
    filter: { type: 'lowpass', cutoff: 4000, resonance: 0, envAmount: 0, keyTrack: 0 },
    filterEnv: { attack: 0.01, decay: 0.5, sustain: 1, release: 0.3 },
    ampEnv: { attack: 0.03, decay: 0.5, sustain: 0.8, release: 0.3 },
    lfo: { wave: 'sine', rate: 5, target: 'pitch', depth: 0 },
    volume: 0.5,
  },
}

/** Whether the synthesizer plays this kind of track; drums keep their own noise-and-sine kit. */
export function hasSynth(kind: TrackKind | undefined): kind is Exclude<TrackKind, 'Drums'> {
  return kind !== undefined && kind !== 'Drums'
}

export function defaultPatch(kind: TrackKind | undefined): SynthPatch {
  return structuredClone(hasSynth(kind) ? DEFAULTS[kind] : LEAD)
}

/** A plain copy; structuredClone refuses Vue's reactive proxies, which is what an edited patch is. */
export function clonePatch(patch: SynthPatch): SynthPatch {
  return JSON.parse(JSON.stringify(patch)) as SynthPatch
}

function clamp(value: unknown, [min, max]: readonly [number, number], fallback: number): number {
  return typeof value === 'number' && Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : fallback
}

function oneOf<T extends string>(value: unknown, options: readonly T[], fallback: T): T {
  return options.includes(value as T) ? (value as T) : fallback
}

function record(value: unknown): Record<string, unknown> {
  return value && typeof value === 'object' ? (value as Record<string, unknown>) : {}
}

function oscillatorOf(raw: unknown, fallback: Oscillator): Oscillator {
  const value = record(raw)
  return {
    wave: oneOf(value.wave, WAVES, fallback.wave),
    octave: Math.round(clamp(value.octave, RANGES.octave, fallback.octave)),
    detune: clamp(value.detune, RANGES.detune, fallback.detune),
    level: clamp(value.level, RANGES.level, fallback.level),
    width: clamp(value.width, RANGES.width, fallback.width),
    pwm: clamp(value.pwm, RANGES.pwm, fallback.pwm),
  }
}

function envelopeOf(raw: unknown, fallback: Envelope): Envelope {
  const value = record(raw)
  return {
    attack: clamp(value.attack, RANGES.attack, fallback.attack),
    decay: clamp(value.decay, RANGES.decay, fallback.decay),
    sustain: clamp(value.sustain, RANGES.sustain, fallback.sustain),
    release: clamp(value.release, RANGES.release, fallback.release),
  }
}

/** A playable patch out of whatever was stored, with the kind's default for anything missing or broken. */
export function normalizePatch(raw: unknown, kind?: TrackKind): SynthPatch {
  const fallback = defaultPatch(kind)
  const value = record(raw)
  const filter = record(value.filter)
  const lfo = record(value.lfo)
  return {
    osc1: oscillatorOf(value.osc1, fallback.osc1),
    osc2: oscillatorOf(value.osc2, fallback.osc2),
    noise: clamp(value.noise, RANGES.level, fallback.noise),
    filter: {
      type: oneOf(filter.type, FILTER_TYPES, fallback.filter.type),
      cutoff: clamp(filter.cutoff, RANGES.cutoff, fallback.filter.cutoff),
      resonance: clamp(filter.resonance, RANGES.resonance, fallback.filter.resonance),
      envAmount: clamp(filter.envAmount, RANGES.envAmount, fallback.filter.envAmount),
      keyTrack: clamp(filter.keyTrack, RANGES.keyTrack, fallback.filter.keyTrack),
    },
    filterEnv: envelopeOf(value.filterEnv, fallback.filterEnv),
    ampEnv: envelopeOf(value.ampEnv, fallback.ampEnv),
    lfo: {
      wave: oneOf(lfo.wave, WAVES, fallback.lfo.wave),
      rate: clamp(lfo.rate, RANGES.rate, fallback.lfo.rate),
      target: oneOf(lfo.target, LFO_TARGETS, fallback.lfo.target),
      depth: clamp(lfo.depth, RANGES.depth, fallback.lfo.depth),
    },
    volume: clamp(value.volume, RANGES.volume, fallback.volume),
  }
}

/**
 * Schedules an envelope on `param` from `start` to the note's end and its release. The note's length is known in
 * advance, so the level at the moment the key is let go is computed rather than read back: Web Audio cannot report
 * a parameter's scheduled value, and `cancelAndHoldAtTime`, which would, is missing in Firefox. Linear attack, then
 * an exponential fall to the sustain level; `decay` is the time to get about 98 % of the way there.
 *
 * @returns When the release has died away.
 */
function envelope(param: AudioParam, env: Envelope, start: number, end: number, base: number, peak: number): number {
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

export interface SynthNote {
  pitch: number
  /** 0 to 1. */
  level: number
  start: number
  end: number
}

/**
 * Builds one voice for one note and connects it to `destination`. Every source it starts is handed to `started`,
 * so the caller can stop them all at once; the voice takes itself apart when it has sounded.
 */
export function playSynthNote(
  context: BaseAudioContext,
  destination: AudioNode,
  patch: SynthPatch,
  note: SynthNote,
  started: (source: AudioScheduledSourceNode, onEnded: () => void) => void,
): void {
  const { start, end } = note
  const frequency = 440 * 2 ** ((note.pitch - 69) / 12)
  const nyquist = context.sampleRate / 2

  const filter = context.createBiquadFilter()
  filter.type = patch.filter.type
  const tracked = patch.filter.cutoff * 2 ** ((patch.filter.keyTrack * (note.pitch - 60)) / 12)
  filter.frequency.value = Math.min(nyquist, Math.max(20, tracked))
  filter.Q.value = patch.filter.resonance
  // The envelope moves the cutoff in cents, so an octave sounds like an octave whatever the cutoff.
  envelope(filter.detune, patch.filterEnv, start, end, 0, patch.filter.envAmount * 1200)

  // The voice ends with its loudness; whatever the filter still does after that is inaudible.
  const amp = context.createGain()
  const stop = envelope(amp.gain, patch.ampEnv, start, end, 0, note.level * patch.volume)
  filter.connect(amp).connect(destination)

  const sources: AudioScheduledSourceNode[] = []
  const nodes: AudioNode[] = [filter, amp]

  /** Delay times that set pulse widths, with the swing the LFO may give each (seconds). */
  const pulseWidths: { delay: DelayNode; swing: number }[] = []

  for (const osc of [patch.osc1, patch.osc2]) {
    if (osc.level <= 0) {
      continue
    }
    const oscillator = context.createOscillator()
    oscillator.frequency.value = frequency * 2 ** osc.octave
    oscillator.detune.value = osc.detune
    const gain = context.createGain()
    gain.connect(filter)
    nodes.push(gain)
    if (osc.wave === 'square' && (osc.width !== 0.5 || osc.pwm > 0)) {
      // Web Audio has no pulse wave. A sawtooth minus itself delayed by width × period is one: the difference is
      // high for that share of each cycle and low for the rest, and moving the delay moves the width (PWM).
      oscillator.type = 'sawtooth'
      const period = 1 / (oscillator.frequency.value * 2 ** (osc.detune / 1200))
      const delay = context.createDelay(period)
      delay.delayTime.value = osc.width * period
      const invert = context.createGain()
      invert.gain.value = -1
      oscillator.connect(delay).connect(invert).connect(gain)
      // Two sawtooths from -1 to 1 span -2 to 2 together. The difference is high for the rest of the cycle, so it is
      // turned over to make `width` the share that is high, as a pulse width is meant.
      gain.gain.value = -osc.level / 2
      nodes.push(delay, invert)
      if (osc.pwm > 0) {
        // Never all the way to 0 or 100 %, where the wave would fall silent.
        pulseWidths.push({ delay, swing: osc.pwm * Math.min(osc.width - 0.02, 0.98 - osc.width) * period })
      }
    } else {
      oscillator.type = osc.wave
      gain.gain.value = osc.level
    }
    oscillator.connect(gain)
    sources.push(oscillator)
  }

  if (patch.noise > 0) {
    const source = context.createBufferSource()
    source.buffer = noiseBuffer(context)
    source.loop = true
    const gain = context.createGain()
    gain.gain.value = patch.noise
    source.connect(gain).connect(filter)
    sources.push(source)
    nodes.push(gain)
  }

  if (sources.length === 0) {
    amp.disconnect()
    filter.disconnect()
    return
  }

  if (patch.lfo.depth > 0 || pulseWidths.length > 0) {
    const lfo = context.createOscillator()
    lfo.type = patch.lfo.wave
    lfo.frequency.value = patch.lfo.rate
    for (const { delay, swing } of pulseWidths) {
      const pwm = context.createGain()
      pwm.gain.value = swing
      lfo.connect(pwm).connect(delay.delayTime)
      nodes.push(pwm)
    }
    const depth = context.createGain()
    lfo.connect(depth)
    if (patch.lfo.depth === 0) {
      // Only there for the pulse width.
    } else if (patch.lfo.target === 'pitch') {
      depth.gain.value = patch.lfo.depth * 100
      for (const source of sources) {
        if (source instanceof OscillatorNode) {
          depth.connect(source.detune)
        }
      }
    } else if (patch.lfo.target === 'filter') {
      depth.gain.value = patch.lfo.depth * 2400
      depth.connect(filter.detune)
    } else {
      // Tremolo: a gain between amp and the destination swinging around 1 - depth/2 by ±depth/2.
      const tremolo = context.createGain()
      tremolo.gain.value = 1 - patch.lfo.depth / 2
      depth.gain.value = patch.lfo.depth / 2
      depth.connect(tremolo.gain)
      amp.disconnect()
      amp.connect(tremolo).connect(destination)
      nodes.push(tremolo)
    }
    sources.push(lfo)
    nodes.push(depth)
  }

  const onEnded = () => {
    for (const node of nodes) {
      node.disconnect()
    }
  }
  sources.forEach((source, index) => {
    source.start(start)
    source.stop(stop)
    // One source ending takes the voice apart; the others end at the same moment.
    started(source, index === 0 ? onEnded : () => {})
  })
}

const noiseBuffers = new WeakMap<BaseAudioContext, AudioBuffer>()

/** One second of white noise per context, looped by every voice that uses it. */
export function noiseBuffer(context: BaseAudioContext): AudioBuffer {
  let buffer = noiseBuffers.get(context)
  if (!buffer) {
    buffer = context.createBuffer(1, context.sampleRate, context.sampleRate)
    const samples = buffer.getChannelData(0)
    for (let i = 0; i < samples.length; i++) {
      samples[i] = Math.random() * 2 - 1
    }
    noiseBuffers.set(context, buffer)
  }
  return buffer
}
