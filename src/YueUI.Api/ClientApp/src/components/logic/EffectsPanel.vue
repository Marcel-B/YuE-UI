<script setup lang="ts">
import { EFFECT_RANGES, type Effects } from '../../logic/effects'
import { t } from '../../logic/i18n'
import { hertz, percent, seconds } from '../../logic/synthFormats'
import SynthKnob from './SynthKnob.vue'

/** Delay and reverb of a sound, shared by both synthesizers. At 0 % an effect is off. */
const fx = defineModel<Effects>({ required: true })
</script>

<template>
  <div class="sections">
    <section>
      <h3>{{ t('fxDelay') }}</h3>
      <SynthKnob v-model="fx.delay.mix" :label="t('fxAmount')" :min="0" :max="1" :format="percent" />
      <SynthKnob
        v-model="fx.delay.time"
        :label="t('fxTime')"
        :min="EFFECT_RANGES.time[0]"
        :max="EFFECT_RANGES.time[1]"
        :curve="2"
        :format="seconds"
      />
      <SynthKnob
        v-model="fx.delay.feedback"
        :label="t('fxFeedback')"
        :min="EFFECT_RANGES.feedback[0]"
        :max="EFFECT_RANGES.feedback[1]"
        :format="percent"
      />
      <SynthKnob
        v-model="fx.delay.tone"
        :label="t('fxTone')"
        :min="EFFECT_RANGES.tone[0]"
        :max="EFFECT_RANGES.tone[1]"
        :curve="3"
        :format="hertz"
      />
    </section>

    <section>
      <h3>{{ t('fxReverb') }}</h3>
      <SynthKnob v-model="fx.reverb.mix" :label="t('fxAmount')" :min="0" :max="1" :format="percent" />
      <SynthKnob
        v-model="fx.reverb.decay"
        :label="t('fxDecay')"
        :min="EFFECT_RANGES.decay[0]"
        :max="EFFECT_RANGES.decay[1]"
        :curve="2"
        :format="seconds"
      />
      <p class="muted mt-1 mb-0 text-xs">{{ t('fxHint') }}</p>
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
