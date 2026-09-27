<script setup lang="ts">
import Slider from 'primevue/slider'
import { computed, useId } from 'vue'

/**
 * One parameter of the synthesizer: a label, a slider and the value as text. `curve` above 1 gives the low end of the
 * range more of the slider, which times and cutoff need: the difference between 5 and 50 ms matters, between 3.5
 * and 4 s hardly.
 */
const props = withDefaults(
  defineProps<{
    label: string
    min: number
    max: number
    /** Rounds the value; 1 for octaves. */
    step?: number
    curve?: number
    format: (value: number) => string
  }>(),
  { step: 0, curve: 1 },
)

const model = defineModel<number>({ required: true })
const id = useId()

const STEPS = 1000

const position = computed(() => {
  const share = (model.value - props.min) / (props.max - props.min)
  return Math.round(Math.max(0, Math.min(1, share)) ** (1 / props.curve) * STEPS)
})

function update(value: number | number[]): void {
  const share = ((Array.isArray(value) ? value[0]! : value) / STEPS) ** props.curve
  const raw = props.min + share * (props.max - props.min)
  model.value = props.step > 0 ? Math.round(raw / props.step) * props.step : raw
}
</script>

<template>
  <div class="knob">
    <label :id="id" class="text-sm">{{ label }}</label>
    <Slider :model-value="position" :min="0" :max="STEPS" :aria-labelledby="id" @update:model-value="update" />
    <span class="text-right text-sm tabular-nums text-muted-color">{{ format(model) }}</span>
  </div>
</template>

<style scoped>
.knob {
  display: grid;
  grid-template-columns: 6.5rem minmax(0, 1fr) 4.5rem;
  gap: 0.75rem;
  align-items: center;
  min-height: 2rem;
}
</style>
