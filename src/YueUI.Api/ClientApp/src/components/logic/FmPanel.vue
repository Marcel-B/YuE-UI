<script setup lang="ts">
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import { formatNumber, t, type MessageKey } from '../../logic/i18n'
import { ALGORITHMS, FM_LFO_TARGETS, FM_RANGES, type FmPatch } from '../../logic/fm'
import { WAVES } from '../../logic/synth'
import { percent, seconds, signed } from '../../logic/synthFormats'
import AlgorithmDiagram from './AlgorithmDiagram.vue'
import EnvelopeCurve from './EnvelopeCurve.vue'
import SynthKnob from './SynthKnob.vue'
import SynthModule from './SynthModule.vue'
import WaveIcon from './WaveIcon.vue'

/**
 * The FM synthesizer's controls. The four operators share one set of knobs, picked in the algorithm's diagram, as on
 * the hardware, since four full modules would not fit a phone. Laid into `SynthEditor`'s grid like `AnalogPanel`.
 */
const patch = defineModel<FmPatch>({ required: true })

/** The operator the knobs show, 0 to 3. */
const selected = ref(0)

const algorithm = computed(() => ALGORITHMS[patch.value.algorithm - 1] ?? ALGORITHMS[0]!)
const algorithms = ALGORITHMS.map((entry, index) => ({ label: `${index + 1}   ${entry.label}`, value: index + 1 }))

const op = computed(() => patch.value.ops[selected.value]!)
const carrier = computed(() => algorithm.value.carriers.includes(selected.value))
const role = computed(() => {
  if (carrier.value) {
    return t('fmCarrier')
  }
  const targets = algorithm.value.mods.filter(([from]) => from === selected.value).map(([, to]) => to + 1)
  return t('fmModulates', { targets: targets.join(', ') })
})

const targetLabels: Record<(typeof FM_LFO_TARGETS)[number], MessageKey> = {
  pitch: 'synthLfoPitch',
  index: 'fmLfoIndex',
  amp: 'synthLfoAmp',
}
const waveLabels: Record<(typeof WAVES)[number], MessageKey> = {
  sine: 'synthWaveSine',
  triangle: 'synthWaveTriangle',
  sawtooth: 'synthWaveSaw',
  square: 'synthWaveSquare',
}
const targets = computed(() => FM_LFO_TARGETS.map((value) => ({ label: t(targetLabels[value]), value })))
const waves = computed(() => WAVES.map((value) => ({ label: t(waveLabels[value]), value })))

const ratio = (value: number) => `× ${formatNumber(value, value % 1 === 0 ? 0 : 1)}`
</script>

<template>
  <SynthModule :title="t('fmAlgorithm')">
    <template #header>
      <Select
        v-model="patch.algorithm"
        :options="algorithms"
        option-label="label"
        option-value="value"
        :aria-label="t('fmAlgorithm')"
        size="small"
        class="font-mono"
      />
    </template>
    <div class="flex w-full flex-wrap items-center gap-x-3 gap-y-2">
      <AlgorithmDiagram v-model="selected" :algorithm="algorithm" class="grow-0" />
      <div class="flex gap-1">
        <SynthKnob v-model="patch.feedback" :label="t('fmFeedback')" :min="0" :max="1" :format="percent" />
        <SynthKnob v-model="patch.volume" :label="t('synthVolume')" :min="0" :max="1" :format="percent" />
      </div>
    </div>
    <template #after>
      <p class="muted mt-2 mb-0 text-xs">{{ t('fmAlgorithmHint') }}</p>
    </template>
  </SynthModule>

  <SynthModule :title="t('fmOperator', { n: selected + 1 })">
    <template #header>
      <span class="muted text-xs">{{ role }}</span>
    </template>
    <template #before><EnvelopeCurve :envelope="op.env" /></template>
    <SynthKnob
      v-model="op.ratio"
      :label="t('fmRatio')"
      :min="FM_RANGES.ratio[0]"
      :max="FM_RANGES.ratio[1]"
      :step="0.5"
      :curve="2"
      :format="ratio"
    />
    <SynthKnob
      v-model="op.detune"
      :label="t('synthDetune')"
      :min="FM_RANGES.detune[0]"
      :max="FM_RANGES.detune[1]"
      :step="1"
      :format="(v) => signed(v, ' ct')"
    />
    <SynthKnob v-model="op.level" :label="t(carrier ? 'synthLevel' : 'fmIndex')" :min="0" :max="1" :format="percent" />
    <SynthKnob v-model="op.velocity" :label="t('fmVelocity')" :min="0" :max="1" :format="percent" />
    <SynthKnob
      v-model="op.env.attack"
      :label="t('synthAttack')"
      :min="FM_RANGES.attack[0]"
      :max="FM_RANGES.attack[1]"
      :curve="3"
      :format="seconds"
    />
    <SynthKnob
      v-model="op.env.decay"
      :label="t('synthDecay')"
      :min="FM_RANGES.decay[0]"
      :max="FM_RANGES.decay[1]"
      :curve="3"
      :format="seconds"
    />
    <SynthKnob v-model="op.env.sustain" :label="t('synthSustain')" :min="0" :max="1" :format="percent" />
    <SynthKnob
      v-model="op.env.release"
      :label="t('synthRelease')"
      :min="FM_RANGES.release[0]"
      :max="FM_RANGES.release[1]"
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
      :min="FM_RANGES.rate[0]"
      :max="FM_RANGES.rate[1]"
      :curve="2"
      :format="(v) => `${formatNumber(v, 1)} Hz`"
    />
    <SynthKnob v-model="patch.lfo.depth" :label="t('synthDepth')" :min="0" :max="1" :format="percent" />
  </SynthModule>
</template>
