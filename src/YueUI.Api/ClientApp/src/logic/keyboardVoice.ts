import { createEffectsChain, type Effects, type EffectsChain } from './effects'
import { midiAccess } from './midiPlayer'
import { playSynthNote, type SynthPatch } from './synth'
import type { Instrument } from './types'

/**
 * Notes for the instruments page's keyboard: held as long as a key is down, which the preview never needs. The
 * synthesizers schedule a note's whole envelope in advance (see `envelope`), so a held note is scheduled long and
 * cut by a gate after the voice, which fades with the sound's release when the key comes up. The filter envelope's
 * own release is lost that way; for trying a sound out that is not heard.
 */

/** What the keyboard plays: a browser sound, or a MIDI instrument on its port and channel. */
export type KeyboardTarget = { kind: 'sound'; patch: SynthPatch } | { kind: 'midi'; instrument: Instrument }

/** Lifts the key. */
export type Release = () => void

/** Longer than any key is held; after it the note ends by itself. */
const HOLD = 30

interface KeyboardAudio {
  context: AudioContext
  /** The sound's delay and reverb; the voices go into its input. */
  fx: EffectsChain
  /** The keyboard's output, for the oscilloscope. */
  scope: AnalyserNode
}

let audio: KeyboardAudio | null = null

function keyboardAudio(): KeyboardAudio {
  if (!audio) {
    const context = new AudioContext()
    const fx = createEffectsChain(context)
    const master = context.createGain()
    // The preview's headroom for one voice, a little more since only a few keys sound at once.
    master.gain.value = 0.3
    const scope = context.createAnalyser()
    scope.fftSize = 2048
    // The scope reads before the headroom, so a single note fills it.
    fx.output.connect(scope)
    fx.output.connect(master).connect(context.destination)
    audio = { context, fx, scope }
  }
  void audio.context.resume()
  return audio
}

/** Takes over changed delay and reverb at once, so a tail that still rings follows the sliders. */
export function setKeyboardEffects(effects: Effects | undefined): void {
  audio?.fx.set(effects)
}

/** What the keyboard sounds like, for the oscilloscope; null until a key was played. */
export function keyboardAnalyser(): AnalyserNode | null {
  return audio?.scope ?? null
}

/** How long the sound takes to die away once let go. */
function releaseOf(patch: SynthPatch): number {
  return patch.engine === 'fm' ? Math.max(...patch.ops.map((op) => op.env.release)) : patch.ampEnv.release
}

function holdSound(patch: SynthPatch, pitch: number, level: number): Release {
  const { context, fx } = keyboardAudio()
  fx.set(patch.fx)
  const start = context.currentTime + 0.005
  const gate = context.createGain()
  gate.connect(fx.input)
  const sources: AudioScheduledSourceNode[] = []
  playSynthNote(context, gate, patch, { pitch, level, start, end: start + HOLD }, (source, onEnded) => {
    sources.push(source)
    source.onended = onEnded
  })
  return () => {
    const now = context.currentTime
    const release = Math.max(0.01, releaseOf(patch))
    gate.gain.setValueAtTime(1, now)
    gate.gain.setTargetAtTime(0, now, release / 4)
    for (const source of sources) {
      try {
        // A later stop replaces the scheduled one.
        source.stop(now + release)
      } catch {
        // Already over.
      }
    }
    window.setTimeout(() => gate.disconnect(), (release + 0.1) * 1000)
  }
}

async function midiPort(name: string): Promise<MIDIOutput | null> {
  const access = await midiAccess()
  const wanted = name.trim()
  return [...access.outputs.values()].find((port) => (port.name ?? port.id).trim() === wanted) ?? null
}

/**
 * Note on at once, note off when released. The port is looked up per note, since an interface may be plugged in
 * while the page is open; a port that is not there plays nothing.
 */
function holdMidi(instrument: Instrument, pitch: number, level: number): Release {
  const channel = instrument.channel - 1
  const velocity = Math.max(1, Math.min(127, Math.round(level * 127)))
  let released = false
  let port: MIDIOutput | null = null
  void midiPort(instrument.port).then((found) => {
    port = found
    port?.send([0x90 | channel, pitch, velocity])
    // A tap shorter than the lookup lifts the key before it went down.
    if (released) {
      port?.send([0x80 | channel, pitch, 0])
    }
  })
  return () => {
    released = true
    port?.send([0x80 | channel, pitch, 0])
  }
}

/** Plays `pitch` (MIDI note number) on the target until the returned function is called. */
export function hold(target: KeyboardTarget, pitch: number, level: number): Release {
  return target.kind === 'sound' ? holdSound(target.patch, pitch, level) : holdMidi(target.instrument, pitch, level)
}
