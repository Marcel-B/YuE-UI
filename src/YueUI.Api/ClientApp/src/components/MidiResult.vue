<script setup lang="ts">
import { computed, onBeforeUnmount, watch } from 'vue'
import type { MidiTrack } from '../api'
import { t } from '../i18n'
import { openMidiOn } from '../view'

/**
 * A finished MIDI file: saved through a real link (Safari starts a download only from the tap itself, not after the
 * request that made the file), or handed to the backing vocals or the Logic page as their upload.
 */
const props = defineProps<{ track: MidiTrack }>()

const url = computed(() => URL.createObjectURL(props.track.file))
watch(url, (_, before) => URL.revokeObjectURL(before))
onBeforeUnmount(() => URL.revokeObjectURL(url.value))
</script>

<template>
  <div class="flex flex-wrap items-center gap-x-2 gap-y-1">
    <span class="muted text-sm">{{ t('midiNotes', { n: track.notes }) }}</span>
    <Button
      as="a"
      :href="url"
      :download="track.file.name"
      icon="pi pi-download"
      :label="t('midiSave')"
      size="small"
      outlined
    />
    <Button
      icon="pi pi-align-center"
      :label="t('midiToHarmony')"
      size="small"
      text
      @click="openMidiOn('harmony', track.file)"
    />
    <Button icon="pi pi-box" :label="t('midiToLogic')" size="small" text @click="openMidiOn('logic', track.file)" />
  </div>
</template>
