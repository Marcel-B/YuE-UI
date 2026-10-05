<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import { midiSettings } from '../audioMidi'
import { t } from '../i18n'

/** How a track becomes MIDI, shared by the upload and the stems; pitch bends only make sense for one line. */
defineProps<{ idPrefix: string }>()
</script>

<template>
  <div class="flex flex-wrap items-center gap-x-4 gap-y-2">
    <div class="flex items-center gap-2">
      <Checkbox v-model="midiSettings.mono" :input-id="`${idPrefix}-mono`" binary />
      <label :for="`${idPrefix}-mono`" class="text-sm">{{ t('midiMono') }}</label>
    </div>
    <div class="flex items-center gap-2">
      <Checkbox v-model="midiSettings.quantize" :input-id="`${idPrefix}-grid`" binary />
      <label :for="`${idPrefix}-grid`" class="text-sm">{{ t('midiQuantize') }}</label>
    </div>
    <div class="flex items-center gap-2">
      <Checkbox v-model="midiSettings.bends" :input-id="`${idPrefix}-bends`" binary :disabled="!midiSettings.mono" />
      <label :for="`${idPrefix}-bends`" class="text-sm" :class="{ muted: !midiSettings.mono }">{{
        t('midiBends')
      }}</label>
    </div>
  </div>
</template>
