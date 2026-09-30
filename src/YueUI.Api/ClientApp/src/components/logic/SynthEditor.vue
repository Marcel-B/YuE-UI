<script setup lang="ts">
import SelectButton from 'primevue/selectbutton'
import { computed, watch } from 'vue'
import { t } from '../../logic/i18n'
import { clonePatch, normalizePatch, type Engine, type SynthPatch } from '../../logic/synth'
import type { TrackKind } from '../../logic/types'
import AnalogPanel from './AnalogPanel.vue'
import FmPanel from './FmPanel.vue'

/**
 * A browser sound's controls, analog (`AnalogPanel`) or FM (`FmPanel`), with the switch between the two. Used for a
 * track's sound in `SynthDialog` and for a named sound on the instruments page.
 */
const props = defineProps<{ kind?: TrackKind }>()
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

function switchEngine(engine: Engine): void {
  if (engine === patch.value.engine) {
    return
  }
  const next = other?.engine === engine ? other : normalizePatch({ engine }, props.kind)
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

  <FmPanel v-if="patch.engine === 'fm'" v-model="patch" />
  <AnalogPanel v-else v-model="patch" />
</template>
