import {
  clamp,
  envelope,
  envelopeOf,
  ENVELOPE_RANGES,
  oneOf,
  record,
  WAVES,
  type Envelope,
  type Wave,
} from './envelope'
import { normalizeEffects, type Effects } from './effects'
import type { SynthNote } from './synth'
import type { TrackKind } from './types'

/**
 * The second browser synthesizer: four sine operators in frequency modulation, after Yamaha's four-operator FM
 * (DX21, TX81Z). An operator either modulates another one's frequency or is heard (a carrier); the algorithm says
 * which. Each has its own envelope, so a modulator's envelope is how the sound's brightness moves, the way the
 * filter envelope does it in the analog one. Like that one, only for listening in the browser.
 */

export const FM_LFO_TARGETS = ['pitch', 'index', 'amp'] as const
export type FmLfoTarget = (typeof FM_LFO_TARGETS)[number]

export interface Operator {
  /** Frequency as a multiple of the note's, in halves: whole numbers keep the sound harmonic. */
  ratio: number
  /** Cents; a few make two carriers beat, more make a modulator bell-like. */
  detune: number
  /** A carrier's loudness, or how far a modulator bends its target (up to `MAX_INDEX`). */
  level: number
  /** How much a soft note takes off this operator: 0 the same for every note, 1 all the way with the velocity. */
  velocity: number
  env: Envelope
}

export interface FmPatch {
  engine: 'fm'
  /** 1 to 8, see `ALGORITHMS`. */
  algorithm: number
  /** How far operator 4 modulates itself: from a sine towards a sawtooth. */
  feedback: number
  /** Operators 1 to 4. */
  ops: [Operator, Operator, Operator, Operator]
  lfo: {
    wave: Wave
    rate: number
    /** `index` moves the modulators' depth, the FM counterpart of a filter sweep. */
    target: FmLfoTarget
    depth: number
  }
  volume: number
  /** Delay and reverb, see effects.ts. */
  fx?: Effects
}

export interface Algorithm {
  /** As printed on the synths: an arrow for each modulation, carriers side by side. */
  label: string
  /** [modulator, target] as operator indices (0 is operator 1); a modulator is always a higher number. */
  mods: readonly (readonly [number, number])[]
  carriers: readonly number[]
}

/** The TX81Z's eight algorithms; feedback is always on operator 4. */
export const ALGORITHMS: readonly Algorithm[] = [
  {
    label: '4→3→2→1',
    mods: [
      [3, 2],
      [2, 1],
      [1, 0],
    ],
    carriers: [0],
  },
  {
    label: '(3+4)→2→1',
    mods: [
      [3, 1],
      [2, 1],
      [1, 0],
    ],
    carriers: [0],
  },
  {
    label: '(4 + 3→2)→1',
    mods: [
      [3, 0],
      [2, 1],
      [1, 0],
    ],
    carriers: [0],
  },
  {
    label: '(4→3 + 2)→1',
    mods: [
      [3, 2],
      [2, 0],
      [1, 0],
    ],
    carriers: [0],
  },
  {
    label: '2→1 · 4→3',
    mods: [
      [3, 2],
      [1, 0],
    ],
    carriers: [0, 2],
  },
  {
    label: '4→(1 · 2 · 3)',
    mods: [
      [3, 0],
      [3, 1],
      [3, 2],
    ],
    carriers: [0, 1, 2],
  },
  { label: '4→3 · 2 · 1', mods: [[3, 2]], carriers: [0, 1, 2] },
  { label: '1 · 2 · 3 · 4', mods: [], carriers: [0, 1, 2, 3] },
]

export const FM_RANGES = {
  algorithm: [1, ALGORITHMS.length],
  feedback: [0, 1],
  ratio: [0.5, 16],
  detune: [-50, 50],
  level: [0, 1],
  velocity: [0, 1],
  rate: [0.1, 20],
  depth: [0, 1],
  volume: [0, 1],
  ...ENVELOPE_RANGES,
} as const satisfies Record<string, readonly [number, number]>

/** The modulation index a modulator at full level reaches: bright, but short of noise. */
const MAX_INDEX = 8
/** Feedback at 1: close to a sawtooth; much more and the loop turns to noise. */
const MAX_FEEDBACK = 1.5

function op(ratio: number, level: number, env: Envelope, velocity = 0.7, detune = 0): Operator {
  return { ratio, detune, level, velocity, env }
}

const BRASS: FmPatch = {
  engine: 'fm',
  algorithm: 4,
  feedback: 0.4,
  ops: [
    op(1, 0.8, { attack: 0.03, decay: 0.4, sustain: 0.8, release: 0.2 }),
    op(1, 0.55, { attack: 0.08, decay: 0.5, sustain: 0.6, release: 0.2 }, 0.6),
    op(1, 0.3, { attack: 0.05, decay: 0.6, sustain: 0.5, release: 0.2 }),
    op(2, 0.15, { attack: 0.05, decay: 0.6, sustain: 0.5, release: 0.2 }),
  ],
  lfo: { wave: 'sine', rate: 5.5, target: 'pitch', depth: 0.1 },
  volume: 0.7,
}

/** A starting sound per kind of track: brass for the melody, the classic electric piano for chords, and so on. */
const DEFAULTS: Record<Exclude<TrackKind, 'Drums'>, FmPatch> = {
  Melody: BRASS,
  Doubling: { ...BRASS, volume: 0.5 },
  Chords: {
    engine: 'fm',
    algorithm: 5,
    feedback: 0,
    ops: [
      op(1, 0.8, { attack: 0.002, decay: 2.5, sustain: 0, release: 0.6 }),
      op(1, 0.5, { attack: 0.002, decay: 1.2, sustain: 0.1, release: 0.5 }, 0.8),
      op(1, 0.35, { attack: 0.002, decay: 0.8, sustain: 0, release: 0.4 }, 0.8, 3),
      op(14, 0.35, { attack: 0.002, decay: 0.25, sustain: 0, release: 0.2 }, 0.9),
    ],
    lfo: { wave: 'sine', rate: 5, target: 'pitch', depth: 0 },
    volume: 0.6,
  },
  Bass: {
    engine: 'fm',
    algorithm: 3,
    feedback: 0.6,
    ops: [
      op(1, 0.9, { attack: 0.002, decay: 0.8, sustain: 0.7, release: 0.08 }),
      op(1, 0.5, { attack: 0.002, decay: 0.25, sustain: 0.2, release: 0.1 }),
      op(3, 0.15, { attack: 0.002, decay: 0.15, sustain: 0, release: 0.1 }),
      op(1, 0.3, { attack: 0.002, decay: 0.4, sustain: 0.3, release: 0.1 }),
    ],
    lfo: { wave: 'sine', rate: 5, target: 'pitch', depth: 0 },
    volume: 0.85,
  },
  GuideTones: {
    engine: 'fm',
    algorithm: 7,
    feedback: 0,
    ops: [
      op(1, 0.8, { attack: 0.03, decay: 0.5, sustain: 0.8, release: 0.3 }),
      op(2, 0.15, { attack: 0.03, decay: 0.5, sustain: 0.8, release: 0.3 }),
      op(1, 0, { attack: 0.03, decay: 0.5, sustain: 0.8, release: 0.3 }),
      op(1, 0, { attack: 0.03, decay: 0.5, sustain: 0.8, release: 0.3 }),
    ],
    lfo: { wave: 'sine', rate: 5, target: 'pitch', depth: 0 },
    volume: 0.5,
  },
}

export function defaultFmPatch(kind: TrackKind | undefined): FmPatch {
  return structuredClone(kind && kind !== 'Drums' ? DEFAULTS[kind] : BRASS)
}

function operatorOf(raw: unknown, fallback: Operator): Operator {
  const value = record(raw)
  return {
    // Halves, as the slider sets them; anything else would be a sound the editor cannot show.
    ratio: Math.round(clamp(value.ratio, FM_RANGES.ratio, fallback.ratio) * 2) / 2,
    detune: clamp(value.detune, FM_RANGES.detune, fallback.detune),
    level: clamp(value.level, FM_RANGES.level, fallback.level),
    velocity: clamp(value.velocity, FM_RANGES.velocity, fallback.velocity),
    env: envelopeOf(value.env, fallback.env),
  }
}

export function normalizeFmPatch(raw: unknown, kind?: TrackKind): FmPatch {
  const fallback = defaultFmPatch(kind)
  const value = record(raw)
  const ops = Array.isArray(value.ops) ? value.ops : []
  const lfo = record(value.lfo)
  return {
    engine: 'fm',
    algorithm: Math.round(clamp(value.algorithm, FM_RANGES.algorithm, fallback.algorithm)),
    feedback: clamp(value.feedback, FM_RANGES.feedback, fallback.feedback),
    ops: [
      operatorOf(ops[0], fallback.ops[0]),
      operatorOf(ops[1], fallback.ops[1]),
      operatorOf(ops[2], fallback.ops[2]),
      operatorOf(ops[3], fallback.ops[3]),
    ],
    lfo: {
      wave: oneOf(lfo.wave, WAVES, fallback.lfo.wave),
      rate: clamp(lfo.rate, FM_RANGES.rate, fallback.lfo.rate),
      target: oneOf(lfo.target, FM_LFO_TARGETS, fallback.lfo.target),
      depth: clamp(lfo.depth, FM_RANGES.depth, fallback.lfo.depth),
    },
    volume: clamp(value.volume, FM_RANGES.volume, fallback.volume),
    fx: normalizeEffects(value.fx),
  }
}

const feedbackWaves = new WeakMap<BaseAudioContext, Map<number, PeriodicWave>>()

/**
 * Operator 4 with feedback. Web Audio cannot feed a node's output back into its own frequency without a delay of at
 * least one render quantum (128 samples), far too late for a loop inside one cycle. The loop's result is one fixed
 * wave for a given amount, though, so it is computed here once, sample by sample as a DX computes it (the average of
 * the last two outputs, which keeps the loop from ringing), and handed to the oscillator as a periodic wave.
 */
function feedbackWave(context: BaseAudioContext, amount: number): PeriodicWave {
  let waves = feedbackWaves.get(context)
  if (!waves) {
    waves = new Map()
    feedbackWaves.set(context, waves)
  }
  const key = Math.round(amount * 50) / 50
  let wave = waves.get(key)
  if (!wave) {
    const size = 2048
    const beta = key * MAX_FEEDBACK
    const cycle = new Float32Array(size)
    let last = 0
    let before = 0
    // A few cycles, so the loop has settled when the one that is kept starts.
    for (let pass = 0; pass < 4; pass++) {
      for (let n = 0; n < size; n++) {
        const y = Math.sin((2 * Math.PI * n) / size + (beta * (last + before)) / 2)
        before = last
        last = y
        cycle[n] = y
      }
    }
    const harmonics = 128
    const real = new Float32Array(harmonics + 1)
    const imag = new Float32Array(harmonics + 1)
    for (let k = 1; k <= harmonics; k++) {
      let re = 0
      let im = 0
      for (let n = 0; n < size; n++) {
        const phase = (2 * Math.PI * k * n) / size
        re += cycle[n]! * Math.cos(phase)
        im += cycle[n]! * Math.sin(phase)
      }
      real[k] = (2 * re) / size
      imag[k] = (2 * im) / size
    }
    wave = context.createPeriodicWave(real, imag, { disableNormalization: true })
    waves.set(key, wave)
  }
  return wave
}

/** Builds one FM voice for one note; the same contract as `playSynthNote`. */
export function playFmNote(
  context: BaseAudioContext,
  destination: AudioNode,
  patch: FmPatch,
  note: SynthNote,
  started: (source: AudioScheduledSourceNode, onEnded: () => void) => void,
): void {
  const { start, end } = note
  const frequency = 440 * 2 ** ((note.pitch - 69) / 12)
  const algorithm = ALGORITHMS[patch.algorithm - 1] ?? ALGORITHMS[0]!
  const scale = (op: Operator) => 1 - op.velocity * (1 - note.level)

  // Only what can be heard is built: a silent carrier, and every modulator that only reaches silent ones, is left out.
  // Modulators always have the higher number, so one pass from operator 1 up settles it.
  const used = [false, false, false, false]
  for (let index = 0; index < 4; index++) {
    const audible = algorithm.carriers.includes(index)
    const feeds = algorithm.mods.some(([from, to]) => from === index && used[to])
    used[index] = patch.ops[index]!.level > 0 && (audible || feeds)
  }
  const carriers = algorithm.carriers.filter((index) => used[index])
  if (carriers.length === 0) {
    return
  }

  const output = context.createGain()
  output.gain.value = patch.volume / Math.sqrt(carriers.length)
  output.connect(destination)
  const sources: AudioScheduledSourceNode[] = []
  const nodes: AudioNode[] = [output]
  const oscillators: (OscillatorNode | null)[] = [null, null, null, null]
  const outs: (GainNode | null)[] = [null, null, null, null]
  let stop = end

  patch.ops.forEach((op, index) => {
    if (!used[index]) {
      return
    }
    const oscillator = context.createOscillator()
    const opFrequency = frequency * op.ratio
    oscillator.frequency.value = opFrequency
    oscillator.detune.value = op.detune
    if (index === 3 && patch.feedback > 0) {
      oscillator.setPeriodicWave(feedbackWave(context, patch.feedback))
    }
    const out = context.createGain()
    oscillator.connect(out)
    const carrier = algorithm.carriers.includes(index)
    // A modulator's output is added to its target's frequency in Hz: index × its own frequency is the deviation.
    const peak = carrier ? op.level * scale(op) : MAX_INDEX * op.level ** 2 * scale(op) * opFrequency
    const ends = envelope(out.gain, op.env, start, end, 0, peak)
    if (carrier) {
      // The voice lasts as long as a carrier sounds; modulators are cut with it.
      stop = Math.max(stop, ends)
      out.connect(output)
    }
    oscillators[index] = oscillator
    outs[index] = out
    sources.push(oscillator)
    nodes.push(out)
  })

  /** Where the LFO reaches into the modulation depth: a gain after each modulator, swinging around 1. */
  const depths: GainNode[] = []
  for (const [from, to] of algorithm.mods) {
    const out = outs[from]
    const target = oscillators[to]
    if (!out || !target) {
      continue
    }
    if (patch.lfo.target === 'index' && patch.lfo.depth > 0) {
      const depth = context.createGain()
      depth.gain.value = 1 - patch.lfo.depth / 2
      out.connect(depth).connect(target.frequency)
      depths.push(depth)
      nodes.push(depth)
    } else {
      out.connect(target.frequency)
    }
  }

  if (patch.lfo.depth > 0) {
    const lfo = context.createOscillator()
    lfo.type = patch.lfo.wave
    lfo.frequency.value = patch.lfo.rate
    const swing = context.createGain()
    lfo.connect(swing)
    if (patch.lfo.target === 'pitch') {
      swing.gain.value = patch.lfo.depth * 100
      for (const oscillator of oscillators) {
        if (oscillator) {
          swing.connect(oscillator.detune)
        }
      }
    } else if (patch.lfo.target === 'index') {
      swing.gain.value = patch.lfo.depth / 2
      for (const depth of depths) {
        swing.connect(depth.gain)
      }
    } else {
      // Tremolo, as in the analog synthesizer: the output swings around 1 - depth/2 by ±depth/2.
      const tremolo = context.createGain()
      tremolo.gain.value = 1 - patch.lfo.depth / 2
      swing.gain.value = patch.lfo.depth / 2
      swing.connect(tremolo.gain)
      output.disconnect()
      output.connect(tremolo).connect(destination)
      nodes.push(tremolo)
    }
    sources.push(lfo)
    nodes.push(swing)
  }

  const onEnded = () => {
    for (const node of nodes) {
      node.disconnect()
    }
  }
  sources.forEach((source, index) => {
    source.start(start)
    source.stop(stop)
    // All stop together, so the first one ending can take the voice apart.
    started(source, index === 0 ? onEnded : () => {})
  })
}
