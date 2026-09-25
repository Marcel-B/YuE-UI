<script setup lang="ts">
import { defaultSampling, type Sampling, type SamplingPhase } from '../form'
import { locale, t, type MessageKey } from '../i18n'
import FieldHelp from './FieldHelp.vue'
import { InputNumber } from 'primevue'
import FloatLabel from 'primevue/floatlabel'

/** The five values of one of the model's sampling settings, each with its default and documentation. */
const props = defineProps<{ phase: SamplingPhase; errors: Record<string, string[]>; disabled?: boolean }>()
const sampling = defineModel<Sampling>({ required: true })

interface Field {
  key: keyof Sampling
  label: MessageKey
  hint: MessageKey
  more: MessageKey
  min: number
  max: number
  step: number
}

// The ranges YuE2's Sampling accepts (the API checks them as well); top-k capped at a sensible 1000.
const fields: Field[] = [
  {
    key: 'temperature',
    label: 'temperature',
    hint: 'temperatureHint',
    more: 'temperatureMore',
    min: 0,
    max: 5,
    step: 0.05,
  },
  { key: 'topP', label: 'topP', hint: 'topPHint', more: 'topPMore', min: 0.01, max: 1, step: 0.01 },
  { key: 'topK', label: 'topK', hint: 'topKHint', more: 'topKMore', min: 1, max: 1000, step: 1 },
  {
    key: 'repetitionPenalty',
    label: 'repetitionPenalty',
    hint: 'repetitionPenaltyHint',
    more: 'repetitionPenaltyMore',
    min: 0.01,
    max: 5,
    step: 0.005,
  },
  {
    key: 'penaltyWindow',
    label: 'penaltyWindow',
    hint: 'penaltyWindowHint',
    more: 'penaltyWindowMore',
    min: 1,
    max: 100,
    step: 1,
  },
]

function id(field: Field): string {
  return `gen-${props.phase}-${field.key}`
}

function hint(field: Field): string {
  // As the number field shows it: 0,95 in German.
  const value = defaultSampling[props.phase][field.key].toLocaleString(locale.value, { maximumFractionDigits: 3 })
  return `${t(field.hint)} ${t('samplingDefault', { value })}`
}
</script>

<template>
  <div class="grid grid-cols-2 gap-3 gap-y-5">
    <div v-for="field in fields" :key="field.key" class="field">
      <FloatLabel variant="on">
        <InputNumber
          :id="id(field)"
          v-model.number="sampling[field.key]"
          type="number"
          inputmode="decimal"
          :min="field.min"
          :max="field.max"
          :step="field.step"
          :disabled="disabled"
          :pt="{
            pcInputText: {
              root: {
                inputmode: 'decimal',
              },
            },
          }"
          :aria-describedby="`${id(field)}-help`"
        />
        <label :for="id(field)">{{ t(field.label) }}</label>
      </FloatLabel>
      <FieldHelp :id="`${id(field)}-help`" :hint="hint(field)" :more="t(field.more)" />
      <small v-if="errors[`${phase}.${field.key}`]" class="danger">{{
        errors[`${phase}.${field.key}`]!.join(' ')
      }}</small>
    </div>
  </div>
</template>
