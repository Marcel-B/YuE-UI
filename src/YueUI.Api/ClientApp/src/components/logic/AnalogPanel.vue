<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import SelectButton from 'primevue/selectbutton'
import { computed } from 'vue'
import { formatNumber, t, type MessageKey } from '../../logic/i18n'
import {
  FILTER_TYPES,
  LFO_TARGETS,
  RANGES,
  WAVES,
  type AnalogPatch,
  type Envelope,
  type Oscillator,
} from '../../logic/synth'
import { hertz, octaves, percent, seconds, signed, signedPercent } from '../../logic/synthFormats'
import EnvelopeCurve from './EnvelopeCurve.vue'
import SynthKnob from './SynthKnob.vue'
import SynthModule from './SynthModule.vue'
import WaveIcon from './WaveIcon.vue'

/**
 * The analog synthesizer's controls: two oscillators and noise, a filter with its envelope, loudness envelope, LFO.
 * Its modules are laid into `SynthEditor`'s grid beside the effects, so the template has no wrapper of its own.
 */
const patch = defineModel<AnalogPatch>({ required: true })

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

const oscillators = computed<{ key: 'osc1' | 'osc2'; title: MessageKey; value: Oscillator }[]>(() => [
  { key: 'osc1', title: 'synthOsc1', value: patch.value.osc1 },
  { key: 'osc2', title: 'synthOsc2', value: patch.value.osc2 },
])
// The filter's envelope next to the filter, the loudness last, as the signal runs.
const envelopes = computed<{ title: MessageKey; value: Envelope }[]>(() => [
  { title: 'synthFilterEnv', value: patch.value.filterEnv },
  { title: 'synthAmpEnv', value: patch.value.ampEnv },
])
</script>

<template>
  <SynthModule v-for="osc in oscillators" :key="osc.key" :title="t(osc.title)">
    <template #header>
      <SelectButton
        v-model="patch[osc.key].wave"
        :options="oscillatorWaves"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      >
        <template #option="{ option }"><WaveIcon :wave="option.value" :label="option.label" /></template>
      </SelectButton>
    </template>
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
      <label class="switch" :title="t('synthPwmLfo')">
        <span>{{ t('synthPwmLfoOn') }}</span>
        <Checkbox v-model="patch[osc.key].pwmLfo" binary :aria-label="t('synthPwmLfo')" />
      </label>
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
    </template>
    <template v-if="patch[osc.key].wave === 'square'" #after>
      <p class="muted mt-2 mb-0 text-xs">{{ t('synthPwmHint') }}</p>
    </template>
  </SynthModule>

  <SynthModule :title="t('synthFilter')">
    <template #header>
      <SelectButton
        v-model="patch.filter.type"
        :options="filterTypes"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      />
    </template>
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
  </SynthModule>

  <SynthModule v-for="env in envelopes" :key="env.title" :title="t(env.title)">
    <template #before><EnvelopeCurve :envelope="env.value" /></template>
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
  </SynthModule>

  <SynthModule :title="t('synthLfo')">
    <template #header>
      <SelectButton
        v-model="patch.lfo.wave"
        :options="waves"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
      >
        <template #option="{ option }"><WaveIcon :wave="option.value" :label="option.label" /></template>
      </SelectButton>
    </template>
    <template #before>
      <SelectButton
        v-model="patch.lfo.target"
        :options="targets"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        size="small"
        class="mb-2"
      />
    </template>
    <SynthKnob
      v-model="patch.lfo.rate"
      :label="t('synthRate')"
      :min="RANGES.rate[0]"
      :max="RANGES.rate[1]"
      :curve="2"
      :format="(v) => `${formatNumber(v, 1)} Hz`"
    />
    <SynthKnob v-model="patch.lfo.depth" :label="t('synthDepth')" :min="0" :max="1" :format="percent" />
  </SynthModule>

  <SynthModule :title="t('synthMix')">
    <SynthKnob v-model="patch.noise" :label="t('synthNoise')" :min="0" :max="1" :format="percent" />
    <SynthKnob v-model="patch.volume" :label="t('synthVolume')" :min="0" :max="1" :format="percent" />
  </SynthModule>
</template>

<style scoped>
/* The switch for the PWM's LFO, laid out like a knob: name above, the box where the dial would be. */
.switch {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 0.6rem;
  width: 4.25rem;
  font-size: 0.75rem;
  line-height: 1.2;
  cursor: pointer;
}
</style>
