<script setup lang="ts">
import { ref, watch } from 'vue'
import { locale } from '../i18n'

/**
 * A number typed as text. PrimeVue's InputNumber did not work on the iPhone: it rewrites the field on every key
 * and takes its decimal sign from the browser rather than the app's language, so the keypad's comma did nothing.
 * Here comma and point both mean the decimal point, and the model only ever gets a finite number within min..max.
 */
const props = withDefaults(defineProps<{ min: number; max: number; fractionDigits?: number }>(), {
  fractionDigits: 0,
})
const model = defineModel<number>({ required: true })

const text = ref(format(model.value))
// While typing, the text stays as written ("0," on the way to "0,95"); it is formatted again on blur.
const editing = ref(false)

watch([model, locale], () => {
  if (!editing.value) {
    text.value = format(model.value)
  }
})

function format(value: number): string {
  return Number.isFinite(value)
    ? value.toLocaleString(locale.value, { maximumFractionDigits: props.fractionDigits, useGrouping: false })
    : ''
}

/** "0,95", "0.95" and ",95" are 0.95; anything else (empty, "1,2,3", "1e3") is null. */
function parse(value: string): number | null {
  const normalized = value.trim().replace(',', '.')
  if (!/^-?\d*\.?\d*$/.test(normalized) || !/\d/.test(normalized)) {
    return null
  }
  const number = Number(Number(normalized).toFixed(props.fractionDigits))
  return Number.isFinite(number) ? Math.min(props.max, Math.max(props.min, number)) : null
}

function onInput(value: string | undefined) {
  text.value = value ?? ''
  const number = parse(text.value)
  if (number !== null) {
    model.value = number
  }
}

function onBlur() {
  editing.value = false
  // Shows the value that counts: clamped, rounded, or the last valid one if the text was not a number.
  text.value = format(model.value)
}
</script>

<template>
  <InputText
    fluid
    :model-value="text"
    type="text"
    :inputmode="fractionDigits > 0 ? 'decimal' : 'numeric'"
    autocomplete="off"
    @update:model-value="onInput"
    @focus="editing = true"
    @blur="onBlur"
  />
</template>
