import { createSpectrumAnalyser } from '../spectrum'
import { createEffectsChain, type EffectsChain } from './effects'
import { defaultPatch, noiseBuffer, playSynthNote, type SynthPatch } from './synth'
import type { ScoreDocument, VoiceTrack } from './types'

/**
 * Playing the parsed score back in the browser. One scheduler walks the notes in time and hands each one to the
 * output its track is routed to: a MIDI port and channel per track, so every instrument of a hardware setup can
 * sit where it listens, or an oscillator where MIDI is unavailable.
 *
 * Outputs keep their own clocks - Web Audio counts from the audio context, Web MIDI from the performance timer -
 * so the scheduler works in delays from now and each output turns that into its own time.
 */

/** Notes are handed over this far ahead of time, so a busy main thread cannot make them late. */
const LOOKAHEAD_SECONDS = 0.2

/** How often the scheduler looks for notes to hand over. Well below the lookahead, so a tick may be missed. */
const TICK_MS = 40

/** The key of the built-in oscillator output; every other key is a MIDI port id. */
export const AUDIO_OUTPUT = 'audio'

/** Where one track goes. `channel` is 0-based as MIDI has it; the interface shows it 1-based. */
export interface Routing {
  output: string
  channel: number
  muted: boolean
}

/**
 * The routing a track starts with: the same channels the MIDI file and the Logic project use, so the preview
 * matches what the export will play. Drums take the General MIDI drum channel, the rest count up around it.
 */
export function defaultRoutings(voices: VoiceTrack[]): Routing[] {
  let next = 0
  return voices.map((voice) => {
    if (voice.kind === 'Drums') {
      return { output: AUDIO_OUTPUT, channel: 9, muted: false }
    }
    if (next === 9) {
      next++
    }
    return { output: AUDIO_OUTPUT, channel: Math.min(next++, 15), muted: false }
  })
}

export interface ScheduledNote {
  /** Seconds from the start of the song. */
  time: number
  duration: number
  pitch: number
  velocity: number
  channel: number
  output: string
  /**
   * Whether this is a drum hit. The oscillator output needs to know regardless of the MIDI channel the track
   * is routed to, otherwise moving the drums to another channel would turn them into melodic notes.
   */
  percussive: boolean
  /** The track's id, which picks its sound on the browser synthesizer. */
  track: string
}

/** The routed voices as a flat list of notes in playing order; muted tracks are left out. */
export function scheduleOf(
  score: ScoreDocument,
  voices: VoiceTrack[],
  routings: Routing[],
  velocityFallback = 96,
): ScheduledNote[] {
  const perTick = 60 / score.tempoBpm / score.ticksPerQuarterNote
  const notes: ScheduledNote[] = []
  voices.forEach((voice, index) => {
    const routing = routings[index]
    if (!routing || routing.muted) {
      return
    }
    const percussive = voice.kind === 'Drums'
    for (const note of voice.notes) {
      notes.push({
        time: note.startTicks * perTick,
        duration: Math.max(0.02, note.durationTicks * perTick),
        pitch: note.noteNumber,
        velocity: note.velocity ?? velocityFallback,
        channel: routing.channel,
        output: routing.output,
        percussive,
        track: voice.id,
      })
    }
  })
  return notes.sort((a, b) => a.time - b.time || a.pitch - b.pitch)
}

/** Where notes go. `play` takes a delay in seconds from now, which every clock can express. */
export interface Output {
  play(note: ScheduledNote, delaySeconds: number): void
  /** Stops everything sounding right now - the panic button, and what every stop needs. */
  silence(): void
  close(): void
  /** Takes over changed mixer settings; only the browser's own output has a mixer. */
  mixChanged?(): void
  /** Peak levels since the last call, 0 to 1 and above for clipping; only the browser's own output has meters. */
  levels?(): Levels
  /** The mix as the spectrum analyzer hears it; only the browser's own output has one. */
  analyser?(): AnalyserNode
}

/** The mixer's settings as the browser output reads them, looked up whenever they change. */
export interface MixLookup {
  /** Linear gain, 1 = unchanged. */
  volume(track: string): number
  /** -1 left to 1 right. */
  pan(track: string): number
  master(): number
}

export interface Levels {
  tracks: Map<string, number>
  master: number
}

const NEUTRAL_MIX: MixLookup = { volume: () => 1, pan: () => 0, master: () => 1 }

/** Percussion on an oscillator stays an imitation, but filtered noise at least reads as a drum kit. */
const DRUM_VOICES: Record<number, { frequency: number; decay: number; noise: number; highpass: number }> = {
  35: { frequency: 55, decay: 0.28, noise: 0.05, highpass: 0 },
  36: { frequency: 58, decay: 0.26, noise: 0.05, highpass: 0 },
  38: { frequency: 190, decay: 0.16, noise: 0.9, highpass: 900 },
  40: { frequency: 220, decay: 0.14, noise: 0.9, highpass: 1100 },
  42: { frequency: 0, decay: 0.05, noise: 1, highpass: 7000 },
  44: { frequency: 0, decay: 0.06, noise: 1, highpass: 6500 },
  46: { frequency: 0, decay: 0.3, noise: 1, highpass: 6000 },
  49: { frequency: 0, decay: 0.9, noise: 1, highpass: 3500 },
  51: { frequency: 0, decay: 0.5, noise: 1, highpass: 5000 },
}

const DEFAULT_DRUM = { frequency: 160, decay: 0.16, noise: 0.8, highpass: 800 }

/**
 * The sound of a track on the browser synthesizer, looked up for every note, so a change in the editor is heard from
 * the next note on without rebuilding the schedule. Null plays the default.
 */
export type PatchLookup = (track: string) => SynthPatch | null

/** The browser synthesizer (see synth.ts) for pitched notes, filtered noise for the drum channel. */
export function createAudioOutput(patchFor: PatchLookup = () => null, mix: MixLookup = NEUTRAL_MIX): Output {
  const context = new AudioContext()
  /** Headroom: a few voices at full level stay below clipping. */
  const HEADROOM = 0.2
  const master = context.createGain()
  master.gain.value = HEADROOM * mix.master()
  const masterMeter = context.createAnalyser()
  masterMeter.fftSize = 512
  master.connect(masterMeter)
  master.connect(context.destination)
  // The analyzer too hears the mix before the headroom, or a full mix would barely reach its middle.
  const spectrum = createSpectrumAnalyser(context)
  const lift = context.createGain()
  lift.gain.value = 1 / HEADROOM
  master.connect(lift).connect(spectrum)

  /**
   * One channel strip per track, made on its first note: the sound's delay and reverb as inserts, then fader, pan and
   * a meter after both, as on a desk.
   */
  interface Strip {
    fx: EffectsChain
    fader: GainNode
    panner: StereoPannerNode
    meter: AnalyserNode
  }
  const strips = new Map<string, Strip>()
  const samples = new Float32Array(512)

  function strip(track: string): Strip {
    let found = strips.get(track)
    if (!found) {
      const fx = createEffectsChain(context)
      const fader = context.createGain()
      fader.gain.value = mix.volume(track)
      const panner = context.createStereoPanner()
      panner.pan.value = mix.pan(track)
      const meter = context.createAnalyser()
      meter.fftSize = 512
      fx.output.connect(fader).connect(panner).connect(meter)
      panner.connect(master)
      found = { fx, fader, panner, meter }
      strips.set(track, found)
    }
    return found
  }

  function peak(analyser: AnalyserNode): number {
    analyser.getFloatTimeDomainData(samples)
    let max = 0
    for (const sample of samples) {
      max = Math.max(max, Math.abs(sample))
    }
    return max
  }

  /** One second of noise, reused by every drum hit instead of building a buffer per note. */
  const noise = noiseBuffer(context)

  let live: AudioScheduledSourceNode[] = []

  function track(source: AudioScheduledSourceNode, gain: GainNode): void {
    live.push(source)
    source.onended = () => {
      live = live.filter((node) => node !== source)
      gain.disconnect()
    }
  }

  function drum(note: ScheduledNote, start: number, level: number): void {
    const shape = DRUM_VOICES[note.pitch] ?? DEFAULT_DRUM
    const gain = context.createGain()
    gain.gain.setValueAtTime(level * 0.9, start)
    gain.gain.exponentialRampToValueAtTime(0.0001, start + shape.decay)
    gain.connect(strip(note.track).fx.input)

    if (shape.noise > 0) {
      const source = context.createBufferSource()
      source.buffer = noise
      const filter = context.createBiquadFilter()
      filter.type = 'highpass'
      filter.frequency.value = shape.highpass
      const noiseGain = context.createGain()
      noiseGain.gain.value = shape.noise
      source.connect(filter).connect(noiseGain).connect(gain)
      source.start(start)
      source.stop(start + shape.decay)
      track(source, gain)
    }

    if (shape.frequency > 0) {
      // The pitch drop is what makes a kick read as a kick rather than a beep.
      const body = context.createOscillator()
      body.frequency.setValueAtTime(shape.frequency * 2.2, start)
      body.frequency.exponentialRampToValueAtTime(shape.frequency, start + shape.decay * 0.5)
      const bodyGain = context.createGain()
      bodyGain.gain.value = shape.noise > 0.5 ? 0.35 : 1
      body.connect(bodyGain).connect(gain)
      body.start(start)
      body.stop(start + shape.decay)
      track(body, gain)
    }
  }

  return {
    play(note, delay) {
      void context.resume()
      const start = context.currentTime + Math.max(0, delay)
      const level = note.velocity / 127

      if (note.percussive) {
        drum(note, start, level)
        return
      }

      const patch = patchFor(note.track) ?? defaultPatch(undefined)
      const channel = strip(note.track)
      channel.fx.set(patch.fx)
      playSynthNote(
        context,
        channel.fx.input,
        patch,
        { pitch: note.pitch, level, start, end: start + note.duration },
        (source, onEnded) => {
          live.push(source)
          source.onended = () => {
            live = live.filter((node) => node !== source)
            onEnded()
          }
        },
      )
    },
    silence() {
      for (const source of live) {
        try {
          source.stop()
        } catch {
          // Already stopped; nothing to do.
        }
      }
      live = []
    },
    close() {
      this.silence()
      void context.close()
    },
    mixChanged() {
      // A short glide, so dragging a fader does not click.
      const now = context.currentTime
      master.gain.setTargetAtTime(HEADROOM * mix.master(), now, 0.02)
      for (const [track, { fader, panner }] of strips) {
        fader.gain.setTargetAtTime(mix.volume(track), now, 0.02)
        panner.pan.setTargetAtTime(mix.pan(track), now, 0.02)
      }
    },
    levels() {
      // The master meter reads before the headroom, so a full mix shows as full.
      return {
        tracks: new Map([...strips].map(([track, entry]) => [track, peak(entry.meter)])),
        master: peak(masterMeter) / HEADROOM,
      }
    },
    analyser: () => spectrum,
  }
}

/** Sends note on and note off to a MIDI port. Timestamps are milliseconds on the performance clock. */
export function createMidiOutput(port: MIDIOutput): Output {
  return {
    play(note, delay) {
      const start = performance.now() + Math.max(0, delay) * 1000
      const pitch = Math.max(0, Math.min(127, Math.round(note.pitch)))
      const velocity = Math.max(1, Math.min(127, Math.round(note.velocity)))
      port.send([0x90 | note.channel, pitch, velocity], start)
      port.send([0x80 | note.channel, pitch, 0], start + note.duration * 1000)
    },
    silence() {
      // Drop anything still queued, then lift every key: "all notes off" alone leaves scheduled note-ons
      // untouched, and those would sound after the stop and hang. Every channel, because a track may have
      // been re-routed since the note was sent. clear() is in the Web MIDI spec but missing from the
      // TypeScript DOM types, and absent in some implementations.
      ;(port as MIDIOutput & { clear?: () => void }).clear?.()
      for (let channel = 0; channel < 16; channel++) {
        port.send([0xb0 | channel, 0x7b, 0]) // all notes off
        port.send([0xb0 | channel, 0x78, 0]) // all sound off
      }
    },
    close() {
      this.silence()
    },
  }
}

export interface MidiPort {
  id: string
  name: string
}

export function midiSupported(): boolean {
  return 'requestMIDIAccess' in navigator
}

/**
 * Whether MIDI hardware can be driven from this browser: Chromium (Chrome, Edge) and Firefox since 108, which asks
 * on the first request and installs a small site-permission add-on for it; both only over HTTPS, where the
 * API exists at all. Safari has no Web MIDI. This gates playing through a port and reading the outputs of the
 * machine - not the instrument library, which lives on the server and decides the channels of the MIDI file
 * and the hardware of the Logic project: that is edited everywhere, only with the outputs of the stored
 * instruments to choose from. A feature test rather than the engine, so every browser that has the API gets it.
 */
export function midiUsable(): boolean {
  return midiSupported()
}

/**
 * Whether MIDI may be used without asking. Chrome and Firefox prompt on the first requestMIDIAccess, and a prompt
 * that appears unasked right after a conversion is startling; the preview therefore only requests on demand,
 * unless permission was granted earlier. Where the browser cannot query the "midi" permission it is asked on
 * demand as well.
 */
export async function midiAlreadyAllowed(): Promise<boolean> {
  if (!midiSupported() || !navigator.permissions) {
    return false
  }
  try {
    // "midi" is not among the TypeScript permission names, though Chrome and Firefox implement it.
    const status = await navigator.permissions.query({ name: 'midi' as PermissionName })
    return status.state === 'granted'
  } catch {
    return false
  }
}

let sharedAccess: Promise<MIDIAccess> | null = null

/**
 * The one MIDIAccess of this page, asked for on first use. Firefox gives every requestMIDIAccess its own port
 * objects, and when one of them is garbage-collected it closes the device by its id in the parent process (midir
 * closes per port, not per object), so every other port object for that device stays "open" and sends into
 * nothing: notes played at first and fell silent once an access the page had dropped was collected. One access
 * keeps one set of port objects alive in its maps for the page's lifetime. A refusal is not kept, so asking again
 * after the user allowed it works.
 */
export function midiAccess(): Promise<MIDIAccess> {
  sharedAccess ??= navigator.requestMIDIAccess().catch((error: unknown) => {
    sharedAccess = null
    throw error
  })
  return sharedAccess
}

/** The MIDI outputs the browser offers, or an empty list where Web MIDI is missing or refused. */
export async function listMidiPorts(): Promise<{ ports: MidiPort[]; access: MIDIAccess | null; reason?: string }> {
  if (!midiSupported()) {
    // Safari has no Web MIDI at all; the oscillator covers those browsers.
    return { ports: [], access: null, reason: 'unsupported' }
  }
  try {
    const access = await midiAccess()
    const ports = [...access.outputs.values()].map((port) => ({ id: port.id, name: port.name ?? port.id }))
    return { ports, access }
  } catch {
    return { ports: [], access: null, reason: 'denied' }
  }
}

/**
 * Keeps the outputs the routings use open, so moving a track to another port does not build an audio context
 * or look a port up again on every note.
 */
export class OutputPool {
  private readonly open = new Map<string, Output>()

  constructor(
    private access: MIDIAccess | null,
    private readonly patchFor: PatchLookup = () => null,
    private readonly mix: MixLookup = NEUTRAL_MIX,
  ) {}

  setAccess(access: MIDIAccess | null): void {
    this.access = access
  }

  get(key: string): Output {
    const existing = this.open.get(key)
    if (existing) {
      return existing
    }
    // A port that has gone away falls back to the oscillator rather than leaving the track silent.
    const port = key === AUDIO_OUTPUT ? null : (this.access?.outputs.get(key) ?? null)
    const output = port ? createMidiOutput(port) : createAudioOutput(this.patchFor, this.mix)
    this.open.set(key, output)
    return output
  }

  mixChanged(): void {
    for (const output of this.open.values()) {
      output.mixChanged?.()
    }
  }

  /**
   * The meters of every output that sounds in the browser: its own, and any that stands in for a port that is gone.
   * Empty while nothing has sounded yet.
   */
  levels(): Levels {
    const all: Levels = { tracks: new Map(), master: 0 }
    for (const output of this.open.values()) {
      const levels = output.levels?.()
      if (levels) {
        levels.tracks.forEach((level, track) => all.tracks.set(track, Math.max(level, all.tracks.get(track) ?? 0)))
        all.master = Math.max(all.master, levels.master)
      }
    }
    return all
  }

  /** The analysers of every output that sounds in the browser, for the spectrum analyzer. */
  analysers(): AnalyserNode[] {
    return [...this.open.values()].flatMap((output) => (output.analyser ? [output.analyser()] : []))
  }

  silence(): void {
    for (const output of this.open.values()) {
      output.silence()
    }
  }

  close(): void {
    for (const output of this.open.values()) {
      output.close()
    }
    this.open.clear()
  }
}

export interface Player {
  /** Starts at `fromSeconds`; calling it again while playing restarts there. */
  play(fromSeconds: number): void
  stop(): void
  /** Position in seconds, or null when stopped. */
  position(): number | null
}

/**
 * Hands the notes to their output shortly before they are due. The position is read by the caller on its own
 * animation frame rather than pushed, so drawing and scheduling stay independent.
 */
export function createPlayer(notes: ScheduledNote[], pool: OutputPool, onEnd: () => void): Player {
  const end = notes.reduce((max, note) => Math.max(max, note.time + note.duration), 0)
  const now = () => performance.now() / 1000
  let timer: number | null = null
  let next = 0
  let origin = 0
  let offset = 0

  function tick(): void {
    const elapsed = now() - origin + offset
    while (next < notes.length && notes[next]!.time < elapsed + LOOKAHEAD_SECONDS) {
      const note = notes[next]!
      // Each output turns the delay into its own clock; a note already due starts now, not in the past.
      pool.get(note.output).play(note, Math.max(0, note.time - elapsed))
      next++
    }
    if (elapsed >= end) {
      stop()
      onEnd()
    }
  }

  function stop(): void {
    if (timer !== null) {
      clearInterval(timer)
      timer = null
    }
    pool.silence()
  }

  return {
    play(fromSeconds) {
      stop()
      offset = Math.max(0, fromSeconds)
      origin = now()
      next = notes.findIndex((note) => note.time >= offset)
      if (next < 0) {
        next = notes.length
      }
      tick()
      timer = window.setInterval(tick, TICK_MS)
    },
    stop,
    position: () => (timer === null ? null : now() - origin + offset),
  }
}

/**
 * A short note for checking that a track reaches the instrument it is routed to, on the track's own sound. `pitch`
 * lets a bass be tried where it plays rather than at middle C.
 */
export function testTone(pool: OutputPool, routing: Routing, track: string, percussive = false, pitch = 60): void {
  const note: ScheduledNote = {
    time: 0,
    duration: 0.4,
    pitch: percussive ? 38 : pitch,
    velocity: 100,
    channel: routing.channel,
    output: routing.output,
    percussive,
    track,
  }
  pool.get(routing.output).play(note, 0)
}
