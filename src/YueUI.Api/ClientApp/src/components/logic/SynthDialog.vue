<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { formatNumber, t, type MessageKey } from '../../logic/i18n'
import {
  FILTER_TYPES,
  LFO_TARGETS,
  clonePatch,
  normalizePatch,
  RANGES,
  WAVES,
  type Envelope,
  type Oscillator,
  type SynthPatch,
} from '../../logic/synth'
import {
  deleteNamedSound,
  namedSounds,
  resetTrackSound,
  saveNamedSound,
  setTrackSound,
  soundsError,
  trackKey,
  trackSounds,
} from '../../logic/synths'
import type { TrackKind } from '../../logic/types'
import SynthKnob from './SynthKnob.vue'

/**
 * The browser synthesizer of one track: two oscillators and noise, a filter with its envelope, a loudness envelope and
 * an LFO. Every change is the track's sound at once (heard from the next note, also while the song plays) and saved
 * on the server; a sound can also be kept under a name and given to other tracks.
 */
defineProps<{ playing: boolean }>()

const emit = defineEmits<{
  /** Plays a short phrase on the track's sound, around `pitch`. */
  audition: [track: string, pitch: number]
  /** Starts or stops the song. */
  toggle: []
}>()

const visible = ref(false)
const track = ref('')
const kind = ref<TrackKind>('Melody')
/** Where the track mostly plays, so the phrase sounds in the track's register. */
const pitch = ref(60)
const patch = ref<SynthPatch>(normalizePatch(null))
/** The named sound the track's sound was taken from, if any. */
const preset = ref<string | null>(null)
const name = ref('')
/** The patch as last given to the track, so opening the dialog does not count as a change. */
let applied = ''

function open(trackName: string, trackKind: TrackKind, trackPitch: number): void {
  track.value = trackName
  kind.value = trackKind
  pitch.value = trackPitch
  const sound = trackSounds.value[trackKey(trackName)]
  load(normalizePatch(sound?.patch, trackKind), sound?.preset ?? null)
  name.value = preset.value ?? ''
  visible.value = true
}

defineExpose({ open })

function load(next: SynthPatch, from: string | null): void {
  applied = JSON.stringify(next)
  patch.value = next
  preset.value = from
}

watch(
  patch,
  (value) => {
    const json = JSON.stringify(value)
    if (json !== applied) {
      applied = json
      setTrackSound(track.value, clonePatch(value), preset.value)
    }
  },
  { deep: true },
)

const presetOptions = computed(() => namedSounds.value.map((sound) => ({ label: sound.name, value: sound.name })))

function takePreset(chosen: string | null): void {
  const sound = namedSounds.value.find((entry) => entry.name === chosen)
  if (!sound) {
    return
  }
  const next = normalizePatch(sound.patch, kind.value)
  load(next, sound.name)
  name.value = sound.name
  setTrackSound(track.value, clonePatch(next), sound.name)
}

const canSave = computed(() => name.value.trim().length > 0 && name.value.trim().length <= 64)
const nameExists = computed(() =>
  namedSounds.value.some((entry) => entry.name.toUpperCase() === name.value.trim().toUpperCase()),
)

async function save(): Promise<void> {
  const chosen = name.value.trim()
  await saveNamedSound(chosen, patch.value)
  preset.value = chosen
  setTrackSound(track.value, clonePatch(patch.value), chosen)
}

async function remove(): Promise<void> {
  const chosen = name.value.trim()
  await deleteNamedSound(chosen)
  if (preset.value?.toUpperCase() === chosen.toUpperCase()) {
    preset.value = null
  }
}

async function reset(): Promise<void> {
  await resetTrackSound(track.value)
  load(normalizePatch(null, kind.value), null)
  name.value = ''
}

// ---- Options and formats ------------------------------------------------------------------------------

const waveLabels: Record<(typeof WAVES)[number], MessageKey> = {
  sine: 'synthWaveSine',
  triangle: 'synthWaveTriangle',
  sawtooth: 'synthWaveSaw',
  square: 'synthWaveSquare',
}
const filterLabels: Record<(typeof FILTER_TYPES)[number], MessageKey> = {
  lowpass: 'synthFilterLowpass',
  highpass: 'synthFilterHighpass',
  bandpass: 'synthFilterBandpass',
}
const targetLabels: Record<(typeof LFO_TARGETS)[number], MessageKey> = {
  pitch: 'synthLfoPitch',
  filter: 'synthLfoFilter',
  amp: 'synthLfoAmp',
}
const waves = computed(() => WAVES.map((value) => ({ label: t(waveLabels[value]), value })))
/** For the oscillators the square is the pulse wave, whose width can be set. */
const oscillatorWaves = computed(() =>
  WAVES.map((value) => ({ label: t(value === 'square' ? 'synthWavePulse' : waveLabels[value]), value })),
)
const filterTypes = computed(() => FILTER_TYPES.map((value) => ({ label: t(filterLabels[value]), value })))
const targets = computed(() => LFO_TARGETS.map((value) => ({ label: t(targetLabels[value]), value })))

const percent = (value: number) => `${Math.round(value * 100)} %`
const signedPercent = (value: number) => `${value > 0.005 ? '+' : ''}${Math.round(value * 100)} %`
const seconds = (value: number) => (value < 1 ? `${Math.round(value * 1000)} ms` : `${formatNumber(value, 2)} s`)
const hertz = (value: number) => (value < 1000 ? `${Math.round(value)} Hz` : `${formatNumber(value / 1000, 1)} kHz`)
const signed = (value: number, unit: string, digits = 0) =>
  `${value > 0 ? '+' : ''}${formatNumber(value, digits)}${unit}`
const octaves = (value: number) => signed(value, ` ${t('synthOctaveUnit')}`, 1)

const oscillators = computed<{ key: 'osc1' | 'osc2'; title: MessageKey; value: Oscillator }[]>(() => [
  { key: 'osc1', title: 'synthOsc1', value: patch.value.osc1 },
  { key: 'osc2', title: 'synthOsc2', value: patch.value.osc2 },
])
const envelopes = computed<{ title: MessageKey; value: Envelope }[]>(() => [
  { title: 'synthAmpEnv', value: patch.value.ampEnv },
  { title: 'synthFilterEnv', value: patch.value.filterEnv },
])
</script>

<template>
  <Dialog
    v-model:visible="visible"
    modal
    dismissable-mask
    :header="t('synthTitle', { track })"
    :draggable="false"
    :style="{ width: 'min(52rem, calc(100vw - 1rem))' }"
  >
    <p class="muted mt-0 mb-3 text-sm">{{ t('synthIntro') }}</p>

    <div class="mb-4 flex flex-wrap items-center gap-2">
      <Select
        :model-value="preset"
        :options="presetOptions"
        option-label="label"
        option-value="value"
        :placeholder="namedSounds.length > 0 ? t('synthPresetPick') : t('synthPresetNone')"
        :disabled="namedSounds.length === 0"
        :aria-label="t('synthPreset')"
        size="small"
        class="min-w-40"
        @update:model-value="takePreset"
      />
      <InputText
        v-model="name"
        size="small"
        maxlength="64"
        class="w-40"
        :placeholder="t('synthPresetName')"
        :aria-label="t('synthPresetName')"
      />
      <Button
        size="small"
        severity="secondary"
        outlined
        icon="pi pi-save"
        :label="nameExists ? t('synthPresetReplace') : t('synthPresetSave')"
        :disabled="!canSave"
        @click="save"
      />
      <Button
        v-if="nameExists"
        size="small"
        severity="secondary"
        text
        icon="pi pi-trash"
        :label="t('synthPresetDelete')"
        @click="remove"
      />
    </div>

    <p v-if="soundsError" class="danger text-sm">{{ t('synthError', { message: soundsError }) }}</p>

    <div class="sections">
      <section v-for="osc in oscillators" :key="osc.key">
        <h3>{{ t(osc.title) }}</h3>
        <SelectButton
          v-model="patch[osc.key].wave"
          :options="oscillatorWaves"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          class="mb-2"
        />
        <SynthKnob
          v-model="patch[osc.key].octave"
          :label="t('synthOctave')"
          :min="RANGES.octave[0]"
          :max="RANGES.octave[1]"
          :step="1"
          :format="(v) => signed(v, '')"
        />
        <SynthKnob
          v-model="patch[osc.key].detune"
          :label="t('synthDetune')"
          :min="RANGES.detune[0]"
          :max="RANGES.detune[1]"
          :step="1"
          :format="(v) => signed(v, ' ct')"
        />
        <SynthKnob v-model="patch[osc.key].level" :label="t('synthLevel')" :min="0" :max="1" :format="percent" />
        <template v-if="patch[osc.key].wave === 'square'">
          <SynthKnob
            v-model="patch[osc.key].width"
            :label="t('synthWidth')"
            :min="RANGES.width[0]"
            :max="RANGES.width[1]"
            :step="0.01"
            :format="percent"
          />
          <div class="flex items-center gap-2 pt-1">
            <Checkbox v-model="patch[osc.key].pwmLfo" binary :input-id="`${osc.key}-pwm-lfo`" />
            <label :for="`${osc.key}-pwm-lfo`" class="text-sm">{{ t('synthPwmLfo') }}</label>
          </div>
          <SynthKnob
            v-model="patch[osc.key].pwm"
            :label="t('synthPwm')"
            :min="0"
            :max="1"
            :disabled="!patch[osc.key].pwmLfo"
            :format="percent"
          />
          <SynthKnob
            v-model="patch[osc.key].pwmAmpEnv"
            :label="t('synthPwmAmpEnv')"
            :min="RANGES.pwmEnv[0]"
            :max="RANGES.pwmEnv[1]"
            :step="0.01"
            :format="signedPercent"
          />
          <SynthKnob
            v-model="patch[osc.key].pwmFilterEnv"
            :label="t('synthPwmFilterEnv')"
            :min="RANGES.pwmEnv[0]"
            :max="RANGES.pwmEnv[1]"
            :step="0.01"
            :format="signedPercent"
          />
          <p class="muted mt-1 mb-0 text-xs">{{ t('synthPwmHint') }}</p>
        </template>
      </section>

      <section>
        <h3>{{ t('synthFilter') }}</h3>
        <SelectButton
          v-model="patch.filter.type"
          :options="filterTypes"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          class="mb-2"
        />
        <SynthKnob
          v-model="patch.filter.cutoff"
          :label="t('synthCutoff')"
          :min="RANGES.cutoff[0]"
          :max="RANGES.cutoff[1]"
          :curve="3"
          :format="hertz"
        />
        <SynthKnob
          v-model="patch.filter.resonance"
          :label="t('synthResonance')"
          :min="RANGES.resonance[0]"
          :max="RANGES.resonance[1]"
          :step="0.1"
          :format="(v) => formatNumber(v, 1)"
        />
        <SynthKnob
          v-model="patch.filter.envAmount"
          :label="t('synthEnvAmount')"
          :min="RANGES.envAmount[0]"
          :max="RANGES.envAmount[1]"
          :step="0.1"
          :format="octaves"
        />
        <SynthKnob v-model="patch.filter.keyTrack" :label="t('synthKeyTrack')" :min="0" :max="1" :format="percent" />
      </section>

      <section v-for="env in envelopes" :key="env.title">
        <h3>{{ t(env.title) }}</h3>
        <SynthKnob
          v-model="env.value.attack"
          :label="t('synthAttack')"
          :min="RANGES.attack[0]"
          :max="RANGES.attack[1]"
          :curve="3"
          :format="seconds"
        />
        <SynthKnob
          v-model="env.value.decay"
          :label="t('synthDecay')"
          :min="RANGES.decay[0]"
          :max="RANGES.decay[1]"
          :curve="3"
          :format="seconds"
        />
        <SynthKnob v-model="env.value.sustain" :label="t('synthSustain')" :min="0" :max="1" :format="percent" />
        <SynthKnob
          v-model="env.value.release"
          :label="t('synthRelease')"
          :min="RANGES.release[0]"
          :max="RANGES.release[1]"
          :curve="3"
          :format="seconds"
        />
      </section>

      <section>
        <h3>{{ t('synthLfo') }}</h3>
        <SelectButton
          v-model="patch.lfo.target"
          :options="targets"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          class="mb-2"
        />
        <SelectButton
          v-model="patch.lfo.wave"
          :options="waves"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          size="small"
          class="mb-2"
        />
        <SynthKnob
          v-model="patch.lfo.rate"
          :label="t('synthRate')"
          :min="RANGES.rate[0]"
          :max="RANGES.rate[1]"
          :curve="2"
          :format="(v) => `${formatNumber(v, 1)} Hz`"
        />
        <SynthKnob v-model="patch.lfo.depth" :label="t('synthDepth')" :min="0" :max="1" :format="percent" />
      </section>

      <section>
        <h3>{{ t('synthMix') }}</h3>
        <SynthKnob v-model="patch.noise" :label="t('synthNoise')" :min="0" :max="1" :format="percent" />
        <SynthKnob v-model="patch.volume" :label="t('synthVolume')" :min="0" :max="1" :format="percent" />
      </section>
    </div>

    <template #footer>
      <div class="flex w-full flex-wrap items-center justify-between gap-2">
        <Button
          size="small"
          severity="secondary"
          text
          icon="pi pi-refresh"
          :label="t('synthReset')"
          :title="t('synthResetTitle')"
          @click="reset"
        />
        <div class="flex flex-wrap gap-2">
          <Button
            size="small"
            severity="secondary"
            outlined
            icon="pi pi-volume-up"
            :label="t('synthAudition')"
            @click="emit('audition', track, pitch)"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            :icon="playing ? 'pi pi-stop' : 'pi pi-play'"
            :label="playing ? t('previewStop') : t('synthPlaySong')"
            @click="emit('toggle')"
          />
          <Button size="small" :label="t('synthClose')" @click="visible = false" />
        </div>
      </div>
    </template>
  </Dialog>
</template>

<style scoped>
.sections {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(min(100%, 22rem), 1fr));
  gap: 1rem 1.5rem;
}

h3 {
  margin: 0 0 0.5rem;
  font-size: 0.95rem;
}
</style>
