<script setup lang="ts">
import SelectButton from 'primevue/selectbutton'
import { computed, watch } from 'vue'
import { t } from '../../logic/i18n'
import { NO_EFFECTS } from '../../logic/effects'
import { clonePatch, normalizePatch, type Engine, type SynthPatch } from '../../logic/synth'
import type { TrackKind } from '../../logic/types'
import AnalogPanel from './AnalogPanel.vue'
import EffectsPanel from './EffectsPanel.vue'
import FmPanel from './FmPanel.vue'
import SynthScope from './SynthScope.vue'

/**
 * A browser sound's controls, analog (`AnalogPanel`) or FM (`FmPanel`), with the switch between the two, its effects
 * (`EffectsPanel`) and, where the caller can hear it, an oscilloscope. Used for a track's sound in `SynthDialog` and
 * for a named sound on the instruments page.
 */
const props = defineProps<{
  kind?: TrackKind
  /** Where the sound plays, for the oscilloscope; none, no scope. */
  scope?: () => AnalyserNode[]
}>()
const patch = defineModel<SynthPatch>({ required: true })

const engines = computed<{ label: string; value: Engine }[]>(() => [
  { label: t('synthEngineAnalog'), value: 'analog' },
  { label: t('synthEngineFm'), value: 'fm' },
])

/** The sound of the other engine as it was left, so switching back and forth in one sitting loses nothing. */
let other: SynthPatch | null = null
let switching = false

// Another sound loaded from outside starts afresh; only the switch below keeps the other engine's sound.
watch(
  () => patch.value,
  () => {
    if (!switching) {
      other = null
    }
    switching = false
  },
)

// Sounds are normalized before they reach the editor, so `fx` is there; this only covers one that was not.
watch(
  () => patch.value,
  (value) => {
    value.fx ??= structuredClone(NO_EFFECTS)
  },
  { immediate: true },
)
const effects = computed(() => patch.value.fx ?? NO_EFFECTS)

function switchEngine(engine: Engine): void {
  if (engine === patch.value.engine) {
    return
  }
  // The effects are the sound's room, not its engine; they stay.
  const next = { ...(other?.engine === engine ? other : normalizePatch({ engine }, props.kind)), fx: patch.value.fx }
  other = clonePatch(patch.value)
  switching = true
  patch.value = next
}
</script>

<template>
  <SelectButton
    :model-value="patch.engine"
    :options="engines"
    option-label="label"
    option-value="value"
    :allow-empty="false"
    size="small"
    class="mb-3"
    :aria-label="t('synthEngine')"
    @update:model-value="switchEngine"
  />
  <p class="muted mt-0 mb-3 text-xs">{{ t(patch.engine === 'fm' ? 'synthEngineFmHint' : 'synthEngineAnalogHint') }}</p>

  <SynthScope v-if="scope" :sources="scope" class="mb-3" />

  <FmPanel v-if="patch.engine === 'fm'" v-model="patch" />
  <AnalogPanel v-else v-model="patch" />

  <h3 class="effects-title">{{ t('fxTitle') }}</h3>
  <EffectsPanel :model-value="effects" />
</template>

<style scoped>
.effects-title {
  margin: 1.25rem 0 0.5rem;
  padding-top: 1rem;
  border-top: 1px solid var(--border);
  font-size: 1rem;
}
</style>
