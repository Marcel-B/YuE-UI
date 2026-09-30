import { clamp, record } from './envelope'

/**
 * Delay and reverb of a browser sound. They belong to the sound, like on a hardware synthesizer, but cannot be built
 * per note as the voice is: a tail has to ring on after its note ends and the next note has to add to it. So each
 * track (and the instruments page's keyboard) has one `EffectsChain` after its voices, which takes the settings of
 * the sound it plays with every note. Both are sends: the dry sound passes unchanged, `mix` sets how much of the
 * effect is added, and 0 builds nothing into the signal.
 */
export interface Effects {
  delay: {
    mix: number
    /** Seconds between repeats. */
    time: number
    /** How much of each repeat comes back, 0 to 0.9 so it always dies away. */
    feedback: number
    /** Lowpass in the loop: every repeat a little darker, as on a tape or bucket-brigade delay. */
    tone: number
  }
  reverb: {
    mix: number
    /** Seconds until the tail has fallen by 60 dB. */
    decay: number
  }
}

export const EFFECT_RANGES = {
  mix: [0, 1],
  time: [0.02, 1.5],
  feedback: [0, 0.9],
  tone: [500, 12000],
  decay: [0.3, 8],
} as const satisfies Record<string, readonly [number, number]>

export const NO_EFFECTS: Effects = {
  delay: { mix: 0, time: 0.35, feedback: 0.35, tone: 4000 },
  reverb: { mix: 0, decay: 2 },
}

export function normalizeEffects(raw: unknown): Effects {
  const value = record(raw)
  const delay = record(value.delay)
  const reverb = record(value.reverb)
  const fallback = NO_EFFECTS
  return {
    delay: {
      mix: clamp(delay.mix, EFFECT_RANGES.mix, fallback.delay.mix),
      time: clamp(delay.time, EFFECT_RANGES.time, fallback.delay.time),
      feedback: clamp(delay.feedback, EFFECT_RANGES.feedback, fallback.delay.feedback),
      tone: clamp(delay.tone, EFFECT_RANGES.tone, fallback.delay.tone),
    },
    reverb: {
      mix: clamp(reverb.mix, EFFECT_RANGES.mix, fallback.reverb.mix),
      decay: clamp(reverb.decay, EFFECT_RANGES.decay, fallback.reverb.decay),
    },
  }
}

const impulses = new WeakMap<BaseAudioContext, Map<number, AudioBuffer>>()

/**
 * A room as an impulse response: stereo noise, different per side so the tail spreads, falling exponentially to
 * -60 dB over `decay`. Real rooms are denser early and darker late; for hearing a sound in some space this does,
 * and it costs no download. Cached per context and per decay in tenths of a second, since a slider drag would
 * otherwise compute a new one on every step.
 */
function impulse(context: BaseAudioContext, decay: number): AudioBuffer {
  const key = Math.round(decay * 10) / 10
  let cache = impulses.get(context)
  if (!cache) {
    cache = new Map()
    impulses.set(context, cache)
  }
  let buffer = cache.get(key)
  if (!buffer) {
    const length = Math.ceil(context.sampleRate * key)
    buffer = context.createBuffer(2, length, context.sampleRate)
    // ln(1000): the level after `decay` seconds is 1/1000, i.e. -60 dB.
    const fall = Math.log(1000) / length
    for (let channel = 0; channel < 2; channel++) {
      const data = buffer.getChannelData(channel)
      for (let i = 0; i < length; i++) {
        data[i] = (Math.random() * 2 - 1) * Math.exp(-fall * i)
      }
    }
    cache.set(key, buffer)
  }
  return buffer
}

export interface EffectsChain {
  /** Where the voices go. */
  input: AudioNode
  /** Dry plus effects, to be connected onwards. */
  output: AudioNode
  /** Takes over a sound's settings; cheap when nothing changed, so it can be called for every note. */
  set(effects: Effects | undefined): void
}

export function createEffectsChain(context: BaseAudioContext): EffectsChain {
  const input = context.createGain()
  const output = context.createGain()
  input.connect(output)

  const delaySend = context.createGain()
  delaySend.gain.value = 0
  const delay = context.createDelay(EFFECT_RANGES.time[1])
  const feedback = context.createGain()
  const tone = context.createBiquadFilter()
  tone.type = 'lowpass'
  input.connect(delaySend).connect(delay).connect(tone).connect(output)
  tone.connect(feedback).connect(delay)

  const reverbSend = context.createGain()
  reverbSend.gain.value = 0
  const convolver = context.createConvolver()
  // The impulse is scaled by its own energy otherwise, which makes a long room as loud as a short one.
  convolver.normalize = false
  // Unnormalized noise is far louder than the dry sound; this brings a full mix to about the dry level.
  const reverbLevel = context.createGain()
  reverbLevel.gain.value = 0.15
  input.connect(reverbSend).connect(convolver).connect(reverbLevel).connect(output)

  let applied = ''
  let decay = -1

  return {
    input,
    output,
    set(effects) {
      const fx = effects ?? NO_EFFECTS
      const json = JSON.stringify(fx)
      if (json === applied) {
        return
      }
      applied = json
      const now = context.currentTime
      // Short glides, so a slider drag while a tail rings does not click.
      delaySend.gain.setTargetAtTime(fx.delay.mix, now, 0.02)
      delay.delayTime.setTargetAtTime(fx.delay.time, now, 0.05)
      feedback.gain.setTargetAtTime(fx.delay.feedback, now, 0.02)
      tone.frequency.setTargetAtTime(fx.delay.tone, now, 0.02)
      reverbSend.gain.setTargetAtTime(fx.reverb.mix, now, 0.02)
      // An empty convolver costs nothing; a room is only made once reverb is turned up.
      const wanted = Math.round(fx.reverb.decay * 10) / 10
      if (fx.reverb.mix > 0 && wanted !== decay) {
        decay = wanted
        convolver.buffer = impulse(context, wanted)
      }
    },
  }
}
