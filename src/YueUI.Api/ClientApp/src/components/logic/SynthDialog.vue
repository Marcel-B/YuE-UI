<script setup lang="ts">
import Dialog from 'primevue/dialog'
import SelectButton from 'primevue/selectbutton'
import { computed, ref, watch } from 'vue'
import { t } from '../../logic/i18n'
import { clonePatch, normalizePatch, type Engine, type SynthPatch } from '../../logic/synth'
import {
  deleteNamedSound,
  namedSounds,
  resetTrackSound,
  saveNamedSound,
  setTrackSound,
  soundsError,
  trackKey,
  trackSounds,
} from '../../logic/synths'
import type { TrackKind } from '../../logic/types'
import AnalogPanel from './AnalogPanel.vue'
import FmPanel from './FmPanel.vue'

/**
 * The browser synthesizer of one track, analog (`AnalogPanel`) or FM (`FmPanel`). Every change is the track's sound at once (heard from the next note, also while the song plays) and saved
 * on the server; a sound can also be kept under a name and given to other tracks.
 */
defineProps<{ playing: boolean }>()

const emit = defineEmits<{
  /** Plays a short phrase on the track's sound, around `pitch`. */
  audition: [track: string, pitch: number]
  /** Starts or stops the song. */
  toggle: []
}>()

const visible = ref(false)
const track = ref('')
const kind = ref<TrackKind>('Melody')
/** Where the track mostly plays, so the phrase sounds in the track's register. */
const pitch = ref(60)
const patch = ref<SynthPatch>(normalizePatch(null))
/** The named sound the track's sound was taken from, if any. */
const preset = ref<string | null>(null)
const name = ref('')
/** The patch as last given to the track, so opening the dialog does not count as a change. */
let applied = ''

function open(trackName: string, trackKind: TrackKind, trackPitch: number): void {
  track.value = trackName
  kind.value = trackKind
  pitch.value = trackPitch
  const sound = trackSounds.value[trackKey(trackName)]
  load(normalizePatch(sound?.patch, trackKind), sound?.preset ?? null)
  name.value = preset.value ?? ''
  visible.value = true
}

defineExpose({ open })

function load(next: SynthPatch, from: string | null): void {
  applied = JSON.stringify(next)
  patch.value = next
  preset.value = from
  other = null
}

const engines = computed<{ label: string; value: Engine }[]>(() => [
  { label: t('synthEngineAnalog'), value: 'analog' },
  { label: t('synthEngineFm'), value: 'fm' },
])

/** The sound of the other engine as it was left, so switching back and forth in one sitting loses nothing. */
let other: SynthPatch | null = null

function switchEngine(engine: Engine): void {
  if (engine === patch.value.engine) {
    return
  }
  const next = other?.engine === engine ? other : normalizePatch({ engine }, kind.value)
  other = clonePatch(patch.value)
  patch.value = next
}

watch(
  patch,
  (value) => {
    const json = JSON.stringify(value)
    if (json !== applied) {
      applied = json
      setTrackSound(track.value, clonePatch(value), preset.value)
    }
  },
  { deep: true },
)

const presetOptions = computed(() => namedSounds.value.map((sound) => ({ label: sound.name, value: sound.name })))

function takePreset(chosen: string | null): void {
  const sound = namedSounds.value.find((entry) => entry.name === chosen)
  if (!sound) {
    return
  }
  const next = normalizePatch(sound.patch, kind.value)
  load(next, sound.name)
  name.value = sound.name
  setTrackSound(track.value, clonePatch(next), sound.name)
}

const canSave = computed(() => name.value.trim().length > 0 && name.value.trim().length <= 64)
const nameExists = computed(() =>
  namedSounds.value.some((entry) => entry.name.toUpperCase() === name.value.trim().toUpperCase()),
)

async function save(): Promise<void> {
  const chosen = name.value.trim()
  await saveNamedSound(chosen, patch.value)
  preset.value = chosen
  setTrackSound(track.value, clonePatch(patch.value), chosen)
}

async function remove(): Promise<void> {
  const chosen = name.value.trim()
  await deleteNamedSound(chosen)
  if (preset.value?.toUpperCase() === chosen.toUpperCase()) {
    preset.value = null
  }
}

async function reset(): Promise<void> {
  await resetTrackSound(track.value)
  load(normalizePatch(null, kind.value), null)
  name.value = ''
}
</script>

<template>
  <Dialog
    v-model:visible="visible"
    modal
    dismissable-mask
    :header="t('synthTitle', { track })"
    :draggable="false"
    :style="{ width: 'min(52rem, calc(100vw - 1rem))' }"
  >
    <p class="muted mt-0 mb-3 text-sm">{{ t('synthIntro') }}</p>

    <div class="mb-4 flex flex-wrap items-center gap-2">
      <Select
        :model-value="preset"
        :options="presetOptions"
        option-label="label"
        option-value="value"
        :placeholder="namedSounds.length > 0 ? t('synthPresetPick') : t('synthPresetNone')"
        :disabled="namedSounds.length === 0"
        :aria-label="t('synthPreset')"
        size="small"
        class="min-w-40"
        @update:model-value="takePreset"
      />
      <InputText
        v-model="name"
        size="small"
        maxlength="64"
        class="w-40"
        :placeholder="t('synthPresetName')"
        :aria-label="t('synthPresetName')"
      />
      <Button
        size="small"
        severity="secondary"
        outlined
        icon="pi pi-save"
        :label="nameExists ? t('synthPresetReplace') : t('synthPresetSave')"
        :disabled="!canSave"
        @click="save"
      />
      <Button
        v-if="nameExists"
        size="small"
        severity="secondary"
        text
        icon="pi pi-trash"
        :label="t('synthPresetDelete')"
        @click="remove"
      />
    </div>

    <p v-if="soundsError" class="danger text-sm">{{ t('synthError', { message: soundsError }) }}</p>

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
    <p class="muted mt-0 mb-3 text-xs">
      {{ t(patch.engine === 'fm' ? 'synthEngineFmHint' : 'synthEngineAnalogHint') }}
    </p>

    <FmPanel v-if="patch.engine === 'fm'" v-model="patch" />
    <AnalogPanel v-else v-model="patch" />

    <template #footer>
      <div class="flex w-full flex-wrap items-center justify-between gap-2">
        <Button
          size="small"
          severity="secondary"
          text
          icon="pi pi-refresh"
          :label="t('synthReset')"
          :title="t('synthResetTitle')"
          @click="reset"
        />
        <div class="flex flex-wrap gap-2">
          <Button
            size="small"
            severity="secondary"
            outlined
            icon="pi pi-volume-up"
            :label="t('synthAudition')"
            @click="emit('audition', track, pitch)"
          />
          <Button
            size="small"
            severity="secondary"
            outlined
            :icon="playing ? 'pi pi-stop' : 'pi pi-play'"
            :label="playing ? t('previewStop') : t('synthPlaySong')"
            @click="emit('toggle')"
          />
          <Button size="small" :label="t('synthClose')" @click="visible = false" />
        </div>
      </div>
    </template>
  </Dialog>
</template>
