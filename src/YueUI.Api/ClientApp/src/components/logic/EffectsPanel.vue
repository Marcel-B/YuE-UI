<script setup lang="ts">
import { EFFECT_RANGES, type Effects } from '../../logic/effects'
import { t } from '../../logic/i18n'
import { hertz, percent, seconds } from '../../logic/synthFormats'
import SynthKnob from './SynthKnob.vue'
import SynthModule from './SynthModule.vue'

/** Delay and reverb of a sound, shared by both synthesizers, as two modules in `SynthEditor`'s grid. At 0 % an effect is off. */
const fx = defineModel<Effects>({ required: true })
</script>

<template>
  <SynthModule :title="t('fxDelay')">
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
  </SynthModule>

  <SynthModule :title="t('fxReverb')">
    <SynthKnob v-model="fx.reverb.mix" :label="t('fxAmount')" :min="0" :max="1" :format="percent" />
    <SynthKnob
      v-model="fx.reverb.decay"
      :label="t('fxDecay')"
      :min="EFFECT_RANGES.decay[0]"
      :max="EFFECT_RANGES.decay[1]"
      :curve="2"
      :format="seconds"
    />
    <template #after>
      <p class="muted mt-2 mb-0 text-xs">{{ t('fxHint') }}</p>
    </template>
  </SynthModule>
</template>
