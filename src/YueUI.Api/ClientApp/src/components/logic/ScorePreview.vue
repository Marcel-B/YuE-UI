<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Panel from 'primevue/panel'
import Slider from 'primevue/slider'
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { t } from '../../logic/i18n'
import { effectiveRouting, instrumentOf } from '../../logic/instruments'
import { loadRecordingSettings, loadRoutings, saveRecordingSettings, saveRoutings } from '../../logic/options'
import { decodeRecording, RecordingPlayer, type Recording } from '../../logic/recording'
import {
  contentHeight,
  draw,
  GUTTER_WIDTH,
  lanesOf,
  ticksAtX,
  totalWidth,
  trackColour,
  WAVE_HEIGHT,
  type Waveform,
  xAtTicks,
} from '../../logic/pianoRoll'
import {
  AUDIO_OUTPUT,
  auditionPhrase,
  createPlayer,
  defaultRoutings,
  listMidiPorts,
  midiAlreadyAllowed,
  midiSupported,
  midiUsable,
  OutputPool,
  scheduleOf,
  testTone,
  type MidiPort,
  type Player,
  type Routing,
} from '../../logic/midiPlayer'
import { playableVoices } from '../../logic/score'
import { hasSynth, normalizePatch, type SynthPatch } from '../../logic/synth'
import { loadSounds, mixer, trackKey, trackMix, trackSounds } from '../../logic/synths'
import type { Assignments, Instrument, ScoreDocument, VoiceTrack } from '../../logic/types'
import { registerSource, reducedMotion, setVisual, visuals, type SpectrumSource } from '../../spectrum'
import SpectrumBars from '../SpectrumBars.vue'
import MixerPanel, { type MixerTrack } from './MixerPanel.vue'
import SynthDialog from './SynthDialog.vue'

const props = defineProps<{
  score: ScoreDocument
  includeChords: boolean
  stale: boolean
  /** The instrument library; with none the table offers ports and channels only. */
  instruments: Instrument[]
  /** Track name → instrument id, kept on the server by the app. */
  assignments: Assignments
  /** The song's recording: the URL of a library song's audio.flac, or the FLAC brought along; null without one. */
  recording: string | File | null
}>()

const emit = defineEmits<{
  /** The user picked an instrument for a track, or none. */
  assign: [track: string, instrumentId: number | null]
  manageInstruments: []
}>()

const canvas = ref<HTMLCanvasElement | null>(null)
const viewport = ref<HTMLDivElement | null>(null)
const large = ref(false)
const pxPerBar = ref(32)
const viewportWidth = ref(0)
const playhead = ref<number | null>(null)
const playing = ref(false)
const ports = ref<MidiPort[]>([])
const note = ref<string | null>(null)
const canAskForMidi = ref(false)

// Kept out of Vue's deep reactivity: thousands of notes that nothing renders from directly.
const voices = shallowRef(playableVoices(props.score, props.includeChords))
const lanes = shallowRef(lanesOf(voices.value))

// ---- The recording ----------------------------------------------------------------------------------

const recordingSettings = ref(loadRecordingSettings())
watch(recordingSettings, (value) => saveRecordingSettings(value), { deep: true })
const decoded = shallowRef<Recording | null>(null)
const recordingState = ref<'idle' | 'loading' | 'ready' | 'failed'>('idle')
/** Where the loaded recording came from, so a new score of the same song does not decode it again. */
let loadedFrom: string | File | null = null
let loadToken = 0
let recordingContext: AudioContext | null = null
let recordingPlayer: RecordingPlayer | null = null
const showWave = computed(() => recordingSettings.value.show && decoded.value !== null)
const waveHeight = computed(() => (showWave.value ? WAVE_HEIGHT : 0))

/**
 * Fetches and decodes the recording once it is to be shown. A library song's FLAC is tens of megabytes, which is why
 * nothing is loaded while the recording is switched off.
 */
async function loadRecording(): Promise<void> {
  const from = props.recording
  if (!from || !recordingSettings.value.show || from === loadedFrom) {
    return
  }
  const token = ++loadToken
  forgetRecording()
  loadedFrom = from
  recordingState.value = 'loading'
  try {
    const file = typeof from === 'string' ? await fetchRecording(from) : from
    recordingContext ??= new AudioContext()
    const result = await decodeRecording(file, recordingContext)
    if (token !== loadToken) {
      return
    }
    decoded.value = result
    recordingState.value = result ? 'ready' : 'failed'
    if (result) {
      recordingPlayer = new RecordingPlayer(recordingContext, result.buffer, recordingSettings.value.volume)
      if (playing.value) {
        startRecording(playhead.value ?? 0)
      }
    }
  } catch {
    if (token === loadToken) {
      recordingState.value = 'failed'
    }
  }
}

async function fetchRecording(url: string): Promise<Blob> {
  const response = await fetch(url)
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`)
  }
  return response.blob()
}

function forgetRecording(): void {
  recordingPlayer?.close()
  recordingPlayer = null
  decoded.value = null
  loadedFrom = null
  recordingState.value = 'idle'
}

/** The recording's first sample sits where the count-in ends, so the audio lines up with the notes behind it. */
function startRecording(fromTicks: number): void {
  if (recordingSettings.value.show) {
    recordingPlayer?.start((fromTicks - (props.score.countInTicks ?? 0)) * secondsPerTick.value)
  }
}

watch(
  () => [props.recording, recordingSettings.value.show] as const,
  ([from, show]) => {
    if (!from) {
      loadToken++
      forgetRecording()
    } else if (show) {
      void loadRecording()
    } else {
      recordingPlayer?.stop()
    }
  },
)

watch(
  () => recordingSettings.value.volume,
  (volume) => recordingPlayer?.setVolume(volume),
)

const waveform = computed<Waveform | null>(() =>
  showWave.value && decoded.value
    ? {
        peaks: decoded.value.peaks,
        secondsPerPeak: decoded.value.secondsPerPeak,
        startTicks: props.score.countInTicks ?? 0,
        secondsPerTick: secondsPerTick.value,
      }
    : null,
)

// The waveform takes a lane of its own above the tracks, which moves every lane down.
watch(waveHeight, (height) => {
  lanes.value = lanesOf(voices.value, height)
  requestAnimationFrame(render)
})

const trackIds = computed(() => voices.value.map((voice) => voice.id))
/** The routing chosen by hand per track; an instrument, where one is assigned, overrides it without touching it. */
const routings = ref<Routing[]>(loadRoutings(trackIds.value, defaultRoutings(voices.value)))
/**
 * Assigning an instrument is offered wherever there are instruments: it says which channel a track is written
 * on and which hardware the Logic project addresses, neither of which needs the browser. Only driving a port
 * from here does, which is what `canDrivePorts` is about - without it the preview sounds through the browser.
 */
const canDrivePorts = midiUsable()
const hasInstruments = computed(() => props.instruments.length > 0)
/** What the player uses: the instrument's port and channel where a track has one, the manual routing elsewhere. */
const effective = computed(() =>
  routings.value.map((routing, index) =>
    effectiveRouting(routing, instrumentOf(trackIds.value[index]!, props.assignments, props.instruments), ports.value),
  ),
)

/**
 * Every track's sound on the browser synthesizer, checked once per change rather than per note: its own where it has
 * one, the default of its kind otherwise. The pool looks it up for every note, so an edit is heard at once.
 */
const patches = computed(
  () =>
    new Map<string, SynthPatch>(
      voices.value.map((voice) => [
        trackKey(voice.id),
        normalizePatch(trackSounds.value[trackKey(voice.id)]?.patch, voice.kind),
      ]),
    ),
)
const pool = new OutputPool(null, (track) => patches.value.get(trackKey(track)) ?? null, {
  volume: (track) => trackMix(track).volume,
  pan: (track) => trackMix(track).pan,
  master: () => mixer.value.master,
})
// Fader and pan are heard at once; the schedule stays as it is.
watch(mixer, () => pool.mixChanged(), { deep: true })

/** Tracks on solo; while any is, only those play. Not saved: a solo is for listening now. */
const solos = ref<boolean[]>([])
/** The routing the player uses, with mute and solo applied; a muted track's notes are not scheduled at all. */
const audible = computed(() => {
  const anySolo = solos.value.some(Boolean)
  return effective.value.map((entry, index) => ({
    ...entry.routing,
    muted: entry.routing.muted || (anySolo && !solos.value[index]),
  }))
})
const mixerTracks = computed<MixerTrack[]>(() =>
  trackIds.value.map((id, index) => ({
    id,
    inBrowser: effective.value[index]?.routing.output === AUDIO_OUTPUT,
    muted: routings.value[index]?.muted ?? false,
    solo: solos.value[index] ?? false,
  })),
)

function setMute(index: number, muted: boolean): void {
  const routing = routings.value[index]
  if (routing) {
    routing.muted = muted
  }
}

function setSolo(index: number, solo: boolean): void {
  const next = [...solos.value]
  next[index] = solo
  solos.value = next
}
const synthDialog = ref<InstanceType<typeof SynthDialog> | null>(null)

/** The middle of a track's range, so a test note or phrase sounds where the track plays. */
function registerOf(voice: VoiceTrack | undefined): number {
  const pitches = (voice?.notes ?? []).map((entry) => entry.noteNumber).sort((a, b) => a - b)
  return pitches.length > 0 ? pitches[Math.floor(pitches.length / 2)]! : 60
}

/** The synthesizer is for tracks that sound in the browser; drums keep their kit, hardware its own sound. */
function editableSound(index: number): boolean {
  return hasSynth(voices.value[index]?.kind) && effective.value[index]?.routing.output === AUDIO_OUTPUT
}

function editSound(index: number): void {
  const voice = voices.value[index]
  if (voice) {
    synthDialog.value?.open(voice.id, voice.kind, registerOf(voice))
  }
}

function audition(track: string, pitch: number): void {
  const index = trackIds.value.findIndex((id) => id === track)
  const routing = effective.value[index]?.routing
  if (routing) {
    // A phrase, not the song: pool.silence() first, so it is not buried under notes still sounding.
    pool.silence()
    auditionPhrase(pool, { ...routing, output: AUDIO_OUTPUT }, track, pitch)
  }
}
/** Channels are 0-based in the routing and shown 1-based, as every MIDI device labels them. */
const channels = Array.from({ length: 16 }, (_, index) => ({ label: `${index + 1}`, value: index }))
/** The browser's own synth first, then whatever MIDI outputs are known right now. */
const outputs = computed(() => [
  { label: t('previewOutputAudio'), value: AUDIO_OUTPUT },
  ...ports.value.map((port) => ({ label: port.name, value: port.id })),
])
const instrumentOptions = computed(() => [
  { label: t('previewInstrumentNone'), value: null as number | null },
  ...props.instruments.map((instrument) => ({ label: instrument.name, value: instrument.id as number | null })),
])
let player: Player | null = null
let frame = 0
let scrollTicks = 0

const secondsPerTick = computed(() => 60 / props.score.tempoBpm / props.score.ticksPerQuarterNote)
const contentWidth = computed(() => totalWidth(props.score, pxPerBar.value) + GUTTER_WIDTH)
const height = computed(() => contentHeight(lanes.value, waveHeight.value))

function render(): void {
  if (canvas.value) {
    draw(canvas.value, {
      score: props.score,
      lanes: lanes.value,
      pxPerBar: pxPerBar.value,
      scrollTicks,
      playhead: playhead.value,
      waveform: waveform.value,
      waveformLabel: t('previewRecording'),
    })
  }
}

function onScroll(): void {
  if (viewport.value) {
    scrollTicks = ticksAtX(props.score, viewport.value.scrollLeft, pxPerBar.value)
    render()
  }
}

/** Keeps the playhead in view while playing, without fighting a scroll the user just made. */
function follow(ticks: number): void {
  const element = viewport.value
  if (!element) {
    return
  }
  const x = xAtTicks(props.score, ticks, pxPerBar.value)
  const visible = element.clientWidth - GUTTER_WIDTH
  if (x < element.scrollLeft || x > element.scrollLeft + visible - 40) {
    element.scrollLeft = Math.max(0, x - visible * 0.2)
  }
}

function step(): void {
  const seconds = player?.position() ?? null
  if (seconds === null) {
    playing.value = false
    render()
    return
  }
  playhead.value = seconds / secondsPerTick.value
  follow(playhead.value)
  render()
  frame = requestAnimationFrame(step)
}

function stop(): void {
  cancelAnimationFrame(frame)
  player?.stop()
  recordingPlayer?.stop()
  playing.value = false
  render()
}

/** Throws the player away, so the next play picks up changed routing or a new score. */
function release(): void {
  cancelAnimationFrame(frame)
  player?.stop()
  recordingPlayer?.stop()
  player = null
  playing.value = false
}

function play(fromTicks = playhead.value ?? 0): void {
  if (!player) {
    player = createPlayer(scheduleOf(props.score, voices.value, audible.value), pool, () => {
      recordingPlayer?.stop()
      playing.value = false
      playhead.value = null
      render()
    })
  }
  player.play(fromTicks * secondsPerTick.value)
  startRecording(fromTicks)
  playing.value = true
  cancelAnimationFrame(frame)
  frame = requestAnimationFrame(step)
}

function toggle(): void {
  if (playing.value) {
    stop()
  } else {
    play()
  }
}

/** Back to bar 1, scrolled home; playback carries on from there if it was running. */
function rewind(): void {
  playhead.value = 0
  viewport.value?.scrollTo({ left: 0 })
  if (playing.value) {
    play(0)
  } else {
    render()
  }
}

/** Lifts every key on every output; the way out when an instrument hangs on a note. */
function panic(): void {
  stop()
  pool.silence()
}

/** A click on the roll moves the playhead there, and keeps playing if it was. */
function seek(event: MouseEvent): void {
  const element = viewport.value
  if (!element) {
    return
  }
  const bounds = element.getBoundingClientRect()
  const x = event.clientX - bounds.left - GUTTER_WIDTH + element.scrollLeft
  // Below the lanes is the horizontal scrollbar; dragging it should not move the playhead.
  if (x < 0 || event.clientY - bounds.top > height.value) {
    return
  }
  playhead.value = Math.min(props.score.lengthTicks, ticksAtX(props.score, x, pxPerBar.value))
  if (playing.value) {
    play(playhead.value)
  } else {
    render()
  }
}

/** Sends every track to the same output, the usual first step with a single interface. */
function routeAll(output: string): void {
  routings.value = routings.value.map((routing) => ({ ...routing, output }))
}

/** The canvas only covers what is on screen, so its width follows the viewport rather than the song. */
const observer = new ResizeObserver(() => {
  viewportWidth.value = viewport.value?.clientWidth ?? 0
  requestAnimationFrame(render)
})

async function loadPorts(): Promise<void> {
  const found = await listMidiPorts()
  pool.setAccess(found.access)
  ports.value = found.ports
  canAskForMidi.value = found.access === null && found.reason !== 'unsupported'
  note.value =
    found.reason === 'unsupported' ? t('midiUnsupported') : found.reason === 'denied' ? t('midiDenied') : null

  if (found.access) {
    // Ports come and go while the page is open.
    found.access.onstatechange = async () => {
      ports.value = (await listMidiPorts()).ports
      const gone = routings.value.some(
        (routing) => routing.output !== AUDIO_OUTPUT && !ports.value.some((port) => port.id === routing.output),
      )
      if (gone) {
        routings.value = routings.value.map((routing) =>
          routing.output !== AUDIO_OUTPUT && !ports.value.some((port) => port.id === routing.output)
            ? { ...routing, output: AUDIO_OUTPUT }
            : routing,
        )
      }
    }
  }
}

onMounted(async () => {
  void loadSounds()
  void loadRecording()
  if (viewport.value) {
    observer.observe(viewport.value)
    viewportWidth.value = viewport.value.clientWidth
  }
  // The canvas takes its width from a style Vue has not applied yet on this tick.
  requestAnimationFrame(render)

  if (!midiSupported()) {
    note.value = t('midiUnsupported')
  } else if (await midiAlreadyAllowed()) {
    await loadPorts()
  } else {
    canAskForMidi.value = true
  }
})

/**
 * The synth and the recording play on two audio contexts, so the analyzer and the background get one analyser of each.
 * Notes sent to MIDI ports are not heard here and so not shown.
 */
const spectrumSource: SpectrumSource = {
  analysers: () => [...pool.analysers(), ...(recordingPlayer ? [recordingPlayer.analyser] : [])],
  playing,
}
const unregisterSource = registerSource(spectrumSource)

onBeforeUnmount(() => {
  unregisterSource()
  observer.disconnect()
  release()
  pool.close()
  recordingPlayer?.close()
  void recordingContext?.close().catch(() => {})
  window.removeEventListener('pagehide', stop)
})

// Hardware would keep sounding if the page went away mid-note.
window.addEventListener('pagehide', stop)

watch(
  () => [props.score, props.includeChords] as const,
  ([score, includeChords]) => {
    release()
    playhead.value = null
    voices.value = playableVoices(score, includeChords)
    lanes.value = lanesOf(voices.value, waveHeight.value)
    routings.value = loadRoutings(trackIds.value, defaultRoutings(voices.value))
    solos.value = []
    scrollTicks = 0
    if (viewport.value) {
      viewport.value.scrollLeft = 0
    }
    render()
  },
)

watch(routings, (value) => saveRoutings(trackIds.value, value), { deep: true })

// Switched on while playing, the recording joins at the playhead.
watch(
  () => recordingSettings.value.show,
  (show) => {
    if (show && playing.value) {
      startRecording(playhead.value ?? 0)
    }
  },
)

// Re-routing - by hand, by instrument or by a port coming or going - rebuilds the schedule; playback picks up
// where it was rather than jumping back to the start. Compared as text, so a refreshed port list alone changes nothing.
watch(
  () => JSON.stringify(audible.value),
  () => {
    const resume = playing.value ? (playhead.value ?? 0) : null
    release()
    if (resume !== null) {
      play(resume)
    }
  },
)

function assign(track: string, instrumentId: number | null): void {
  emit('assign', track, instrumentId)
}

// Zooming keeps the bar at the left edge in place; the raw scroll offset would otherwise jump to a
// different part of the song every time the scale changes.
watch(pxPerBar, (value) => {
  const anchor = scrollTicks
  requestAnimationFrame(() => {
    if (viewport.value) {
      viewport.value.scrollLeft = Math.max(0, xAtTicks(props.score, anchor, value))
    }
    onScroll()
  })
})

watch([large, viewportWidth], () => requestAnimationFrame(onScroll))
</script>

<template>
  <!--
    min-w-0: a grid item will not shrink below its content by default, and the content here is the whole song - at
    204 bars well over 6000 pixels, which would drag the entire page wide. This keeps the card inside the column.
  -->
  <Card class="min-w-0" :class="{ 'fixed inset-4 z-20 overflow-auto shadow-2xl': large }">
    <template #title>
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h2 class="m-0">{{ t('previewTitle') }}</h2>
        <div class="flex flex-wrap items-center gap-2">
          <Button
            size="small"
            :icon="playing ? 'pi pi-stop' : 'pi pi-play'"
            :label="playing ? t('previewStop') : t('previewPlay')"
            :aria-pressed="playing"
            @click="toggle"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            icon="pi pi-step-backward"
            :label="t('previewRewind')"
            :title="t('previewRewindTitle')"
            :disabled="!playing && !playhead"
            @click="rewind"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            :label="t('previewPanic')"
            :title="t('previewPanicTitle')"
            @click="panic"
          />
          <div class="w-32 px-2" :title="t('previewZoom')">
            <Slider
              :model-value="pxPerBar"
              :min="6"
              :max="120"
              :step="2"
              :aria-label="t('previewZoom')"
              @update:model-value="pxPerBar = $event as number"
            />
          </div>
          <Button
            size="small"
            severity="secondary"
            outlined
            :icon="large ? 'pi pi-window-minimize' : 'pi pi-window-maximize'"
            :label="large ? t('previewSmaller') : t('previewLarger')"
            :aria-pressed="large"
            @click="large = !large"
          />
        </div>
      </div>
    </template>

    <template #content>
      <p v-if="stale" class="hint warning mt-0">{{ t('stale') }}</p>

      <div class="mb-2 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
        <div v-if="recording" class="flex items-center gap-2">
          <Checkbox v-model="recordingSettings.show" binary input-id="preview-recording" />
          <label for="preview-recording" class="cursor-pointer">{{ t('previewRecordingShow') }}</label>
        </div>
        <div v-if="recordingSettings.show && recordingState === 'ready'" class="flex items-center gap-2">
          <span id="preview-recording-volume" class="text-muted-color">{{ t('previewRecordingVolume') }}</span>
          <Slider
            :model-value="Math.round(recordingSettings.volume * 100)"
            :min="0"
            :max="100"
            aria-labelledby="preview-recording-volume"
            class="w-28"
            @update:model-value="recordingSettings.volume = ($event as number) / 100"
          />
        </div>
        <span v-if="recordingSettings.show && recordingState === 'loading'" class="muted">
          <i class="pi pi-spin pi-spinner mr-1" aria-hidden="true" />{{ t('previewRecordingLoading') }}
        </span>
        <span v-if="recordingSettings.show && recordingState === 'failed'" class="text-(--warning-text)">
          {{ t('previewRecordingFailed') }}
        </span>
        <div class="flex items-center gap-2">
          <Checkbox
            :model-value="visuals.logic"
            binary
            input-id="preview-analyzer"
            @update:model-value="setVisual('logic', $event)"
          />
          <label for="preview-analyzer" class="cursor-pointer">{{ t('previewAnalyzer') }}</label>
        </div>
        <div class="flex items-center gap-2">
          <Checkbox
            :model-value="visuals.background"
            binary
            input-id="preview-background"
            @update:model-value="setVisual('background', $event)"
          />
          <label for="preview-background" class="cursor-pointer">
            {{ reducedMotion ? t('previewBackgroundReduced') : t('previewBackground') }}
          </label>
        </div>
      </div>

      <!-- Above the roll rather than beside it: the roll needs the width, the analyzer only a strip of height. -->
      <div v-if="visuals.logic" class="analyzer mb-2">
        <SpectrumBars :sources="[spectrumSource]" :bars="64" peaks />
      </div>

      <div ref="viewport" class="viewport" @scroll.passive="onScroll" @click="seek">
        <div :style="{ width: `${contentWidth}px`, height: `${height}px` }">
          <canvas ref="canvas" :style="{ width: `${viewportWidth}px`, height: `${height}px` }" />
        </div>
      </div>

      <!-- The panel's content sits in a grid (for its collapse animation), whose item would grow to the table's width. -->
      <Panel :header="t('previewRouting')" toggleable class="mt-3" :pt="{ contentWrapper: { class: 'min-w-0' } }">
        <div class="mb-2 flex flex-wrap items-center gap-3">
          <Button
            v-if="canAskForMidi"
            size="small"
            severity="secondary"
            outlined
            :label="t('previewFindMidi')"
            @click="loadPorts"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            :label="t('instrumentsManage')"
            @click="emit('manageInstruments')"
          />
          <div class="flex items-center gap-2 text-sm text-muted-color">
            <label id="preview-route-all">{{ t('previewRouteAll') }}</label>
            <!-- An action rather than a setting: it always shows the prompt, and picking an output applies it. -->
            <Select
              :model-value="null"
              :options="outputs"
              option-label="label"
              option-value="value"
              :placeholder="t('previewRouteAllPick')"
              aria-labelledby="preview-route-all"
              size="small"
              @update:model-value="routeAll"
            />
          </div>
        </div>

        <div class="relative overflow-x-auto">
          <table class="w-full border-collapse text-sm">
            <thead>
              <tr class="text-left text-xs text-muted-color">
                <th class="py-1.5 pr-2 font-medium">{{ t('previewTrack') }}</th>
                <th v-if="hasInstruments" class="py-1.5 pr-2 font-medium">{{ t('previewInstrument') }}</th>
                <th class="py-1.5 pr-2 font-medium">{{ t('previewOutput') }}</th>
                <th class="py-1.5 pr-2 font-medium">{{ t('previewChannel') }}</th>
                <th class="py-1.5 font-medium">
                  <span class="sr-only">{{ t('previewTest') }}</span>
                </th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="(routing, index) in routings" :key="trackIds[index]" :class="{ 'opacity-50': routing.muted }">
                <td class="py-1 pr-2 align-middle">
                  <div class="flex items-center gap-2 whitespace-nowrap">
                    <Checkbox
                      v-model="routing.muted"
                      binary
                      :true-value="false"
                      :false-value="true"
                      :input-id="`preview-track-${index}`"
                    />
                    <span class="swatch" :style="{ background: trackColour(index) }" />
                    <label :for="`preview-track-${index}`" class="cursor-pointer">{{ trackIds[index] }}</label>
                  </div>
                </td>
                <td v-if="hasInstruments" class="py-1 pr-2 align-middle">
                  <Select
                    :model-value="effective[index]?.instrument?.id ?? null"
                    :options="instrumentOptions"
                    option-label="label"
                    option-value="value"
                    :placeholder="t('previewInstrumentNone')"
                    :aria-label="`${t('previewInstrument')} ${trackIds[index]}`"
                    size="small"
                    class="w-full min-w-32"
                    @update:model-value="assign(trackIds[index]!, $event)"
                  />
                </td>
                <template v-if="effective[index]?.instrument">
                  <td v-if="effective[index]?.port" class="whitespace-nowrap py-1 pr-2 align-middle">
                    {{ effective[index]?.port?.name }}
                  </td>
                  <!-- Without Web MIDI no port is ever found, so naming one that is missing would be misleading. -->
                  <td v-else-if="!canDrivePorts" class="muted whitespace-nowrap py-1 pr-2 align-middle">
                    {{ effective[index]?.instrument?.port }}
                  </td>
                  <td v-else class="py-1 pr-2 align-middle text-(--warning-text)">
                    {{ t('previewInstrumentMissing', { port: effective[index]?.instrument?.port ?? '' }) }}
                  </td>
                  <td class="whitespace-nowrap py-1 pr-2 align-middle">{{ effective[index]!.routing.channel + 1 }}</td>
                </template>
                <template v-else>
                  <td class="py-1 pr-2 align-middle">
                    <Select
                      v-model="routing.output"
                      :options="outputs"
                      option-label="label"
                      option-value="value"
                      :aria-label="`${t('previewOutput')} ${trackIds[index]}`"
                      size="small"
                      class="w-full min-w-32"
                    />
                  </td>
                  <td class="py-1 pr-2 align-middle">
                    <Select
                      v-model="routing.channel"
                      :options="channels"
                      option-label="label"
                      option-value="value"
                      :disabled="routing.output === AUDIO_OUTPUT"
                      :aria-label="`${t('previewChannel')} ${trackIds[index]}`"
                      size="small"
                      class="w-20"
                    />
                  </td>
                </template>
                <td class="py-1 align-middle">
                  <div class="flex gap-1">
                    <Button
                      size="small"
                      severity="secondary"
                      outlined
                      :label="t('previewTest')"
                      @click="
                        testTone(
                          pool,
                          effective[index]!.routing,
                          trackIds[index]!,
                          voices[index]?.kind === 'Drums',
                          registerOf(voices[index]),
                        )
                      "
                    />
                    <Button
                      v-if="editableSound(index)"
                      size="small"
                      severity="secondary"
                      outlined
                      icon="pi pi-sliders-h"
                      :label="t('previewSound')"
                      :title="t('previewSoundTitle')"
                      @click="editSound(index)"
                    />
                  </div>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </Panel>

      <Panel :header="t('mixerTitle')" toggleable class="mt-3" :pt="{ contentWrapper: { class: 'min-w-0' } }">
        <MixerPanel :tracks="mixerTracks" :pool="pool" @mute="setMute" @solo="setSolo" />
        <p class="muted hint mb-0">{{ t('mixerHint') }}</p>
      </Panel>

      <p class="muted hint">{{ note ?? (hasInstruments ? t('previewInstrumentHint') : t('previewHint')) }}</p>
      <SynthDialog ref="synthDialog" :playing="playing" @audition="audition" @toggle="toggle" />
    </template>
  </Card>
</template>

<style scoped>
/*
 * The content element carries the scrollable width; the canvas sticks to the left edge of the scrollport and
 * is only as wide as what is visible, so a long song costs no extra pixels. The piano roll reads its colours
 * (--text, --border, --accent, ...) from the canvas's computed style, which inherits them from :root.
 */
.analyzer {
  height: 5rem;
  padding: 0.375rem;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
}

.viewport {
  max-width: 100%;
  overflow-x: auto;
  overflow-y: hidden;
  border: 1px solid var(--border);
  border-radius: var(--radius-small);
  cursor: crosshair;
}

.viewport canvas {
  position: sticky;
  left: 0;
  display: block;
}

.swatch {
  flex: none;
  width: 0.75rem;
  height: 0.75rem;
  border-radius: 3px;
}
</style>
