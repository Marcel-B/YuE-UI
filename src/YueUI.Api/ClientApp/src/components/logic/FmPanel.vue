<script setup lang="ts">
import SelectButton from 'primevue/selectbutton'
import { computed, ref } from 'vue'
import { formatNumber, t, type MessageKey } from '../../logic/i18n'
import { ALGORITHMS, FM_LFO_TARGETS, FM_RANGES, type FmPatch } from '../../logic/fm'
import { WAVES } from '../../logic/synth'
import { percent, seconds, signed } from '../../logic/synthFormats'
import SynthKnob from './SynthKnob.vue'

/**
 * The FM synthesizer's controls. The four operators share one set of sliders with a switch between them, as on the
 * hardware, since four full columns would not fit a phone.
 */
const patch = defineModel<FmPatch>({ required: true })

/** The operator the sliders show, 0 to 3. */
const selected = ref(0)

const algorithm = computed(() => ALGORITHMS[patch.value.algorithm - 1] ?? ALGORITHMS[0]!)
const algorithms = ALGORITHMS.map((entry, index) => ({ label: `${index + 1}   ${entry.label}`, value: index + 1 }))
/** Carriers are marked, so the switch alone shows which operators are heard. */
const operators = computed(() =>
  [0, 1, 2, 3].map((index) => ({
    label: `${index + 1}${algorithm.value.carriers.includes(index) ? ' ♪' : ''}`,
    value: index,
  })),
)

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
  <div class="sections">
    <section>
      <h3>{{ t('fmAlgorithm') }}</h3>
      <Select
        v-model="patch.algorithm"
        :options="algorithms"
        option-label="label"
        option-value="value"
        :aria-label="t('fmAlgorithm')"
        size="small"
        class="mb-2 w-full font-mono"
      />
      <SynthKnob v-model="patch.feedback" :label="t('fmFeedback')" :min="0" :max="1" :format="percent" />
      <p class="muted mt-1 mb-0 text-xs">{{ t('fmAlgorithmHint') }}</p>
    </section>

    <section>
      <h3>{{ t('fmOperators') }}</h3>
      <SelectButton
        v-model="selected"
        :options="operators"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        :aria-label="t('fmOperators')"
        size="small"
        class="mb-1"
      />
      <p class="muted mt-0 mb-2 text-xs">{{ t('fmOperator', { n: selected + 1 }) }}: {{ role }}</p>
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
      <SynthKnob
        v-model="op.level"
        :label="t(carrier ? 'synthLevel' : 'fmIndex')"
        :min="0"
        :max="1"
        :format="percent"
      />
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
        :min="FM_RANGES.rate[0]"
        :max="FM_RANGES.rate[1]"
        :curve="2"
        :format="(v) => `${formatNumber(v, 1)} Hz`"
      />
      <SynthKnob v-model="patch.lfo.depth" :label="t('synthDepth')" :min="0" :max="1" :format="percent" />
    </section>

    <section>
      <h3>{{ t('synthMix') }}</h3>
      <SynthKnob v-model="patch.volume" :label="t('synthVolume')" :min="0" :max="1" :format="percent" />
    </section>
  </div>
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
