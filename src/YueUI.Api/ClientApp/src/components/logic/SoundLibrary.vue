<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { t } from '../../logic/i18n'
import { clonePatch, normalizePatch, type Engine, type SynthPatch } from '../../logic/synth'
import { deleteNamedSound, loadSounds, namedSounds, saveNamedSound, soundsError } from '../../logic/synths'
import SynthEditor from './SynthEditor.vue'

/**
 * The saved browser sounds: listed, edited and saved under a name here, given to tracks on the Logic page (the
 * track's sound dialog loads them). The sound being edited is the page's keyboard's, so every change is heard at once.
 */
const draft = defineModel<SynthPatch>({ required: true })

/** The saved sound the draft came from, or null for a new one. */
const selected = ref<string | null>(null)
const name = ref('')
/** The draft as last saved or loaded, to tell whether it has changes. */
const saved = ref(JSON.stringify(draft.value))
const busy = ref(false)

onMounted(() => void loadSounds())

const sounds = computed(() =>
  [...namedSounds.value]
    .map((sound) => ({ name: sound.name, engine: normalizePatch(sound.patch).engine }))
    .sort((a, b) => a.name.localeCompare(b.name)),
)

const trimmed = computed(() => name.value.trim())
const exists = computed(() =>
  namedSounds.value.some((sound) => sound.name.toUpperCase() === trimmed.value.toUpperCase()),
)
const changed = computed(() => JSON.stringify(draft.value) !== saved.value || trimmed.value !== (selected.value ?? ''))
const canSave = computed(() => trimmed.value.length > 0 && trimmed.value.length <= 64 && changed.value && !busy.value)

function load(patch: SynthPatch, from: string | null): void {
  draft.value = patch
  saved.value = JSON.stringify(patch)
  selected.value = from
  name.value = from ?? ''
}

function open(soundName: string): void {
  const sound = namedSounds.value.find((entry) => entry.name === soundName)
  if (sound) {
    load(normalizePatch(sound.patch), sound.name)
  }
}

function startNew(engine: Engine): void {
  load(normalizePatch({ engine }, 'Melody'), null)
}

async function save(): Promise<void> {
  busy.value = true
  try {
    await saveNamedSound(trimmed.value, clonePatch(draft.value))
    if (!soundsError.value) {
      saved.value = JSON.stringify(draft.value)
      // The server keeps a name's spelling as first saved; show the entry that was written.
      selected.value = namedSounds.value.find((s) => s.name.toUpperCase() === trimmed.value.toUpperCase())?.name ?? null
      name.value = selected.value ?? trimmed.value
    }
  } finally {
    busy.value = false
  }
}

async function remove(): Promise<void> {
  const chosen = selected.value
  if (!chosen || !window.confirm(t('soundDeleteConfirm', { name: chosen }))) {
    return
  }
  busy.value = true
  try {
    await deleteNamedSound(chosen)
    if (!soundsError.value) {
      startNew(draft.value.engine)
    }
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <div class="grid gap-4 md:grid-cols-[14rem_minmax(0,1fr)]">
    <div class="flex flex-col gap-2">
      <div class="flex flex-wrap gap-2">
        <Button
          size="small"
          severity="secondary"
          outlined
          icon="pi pi-plus"
          :label="t('soundNewAnalog')"
          @click="startNew('analog')"
        />
        <Button
          size="small"
          severity="secondary"
          outlined
          icon="pi pi-plus"
          :label="t('soundNewFm')"
          @click="startNew('fm')"
        />
      </div>
      <nav v-if="sounds.length > 0" class="sound-list" :aria-label="t('soundsTitle')">
        <Button
          v-for="sound in sounds"
          :key="sound.name"
          size="small"
          :severity="sound.name === selected ? undefined : 'secondary'"
          :outlined="sound.name !== selected"
          class="justify-between"
          :aria-current="sound.name === selected ? 'true' : undefined"
          @click="open(sound.name)"
        >
          <span class="truncate">{{ sound.name }}</span>
          <span class="text-xs opacity-75">{{ sound.engine === 'fm' ? 'FM' : t('synthEngineAnalog') }}</span>
        </Button>
      </nav>
      <p v-else class="muted m-0 text-sm">{{ t('synthPresetNone') }}</p>
    </div>

    <div class="min-w-0">
      <div class="mb-3 flex flex-wrap items-center gap-2">
        <InputText
          v-model="name"
          size="small"
          maxlength="64"
          class="w-48"
          :placeholder="t('synthPresetName')"
          :aria-label="t('synthPresetName')"
          @keydown.enter="canSave && save()"
        />
        <Button
          size="small"
          icon="pi pi-save"
          :label="exists ? t('synthPresetReplace') : t('synthPresetSave')"
          :disabled="!canSave"
          :loading="busy"
          @click="save"
        />
        <Button
          v-if="selected"
          size="small"
          severity="secondary"
          text
          icon="pi pi-trash"
          :label="t('synthPresetDelete')"
          :disabled="busy"
          @click="remove"
        />
        <span v-if="changed && trimmed" class="muted text-xs">{{ t('soundUnsaved') }}</span>
      </div>
      <p v-if="soundsError" class="danger text-sm">{{ t('synthError', { message: soundsError }) }}</p>
      <SynthEditor v-model="draft" kind="Melody" />
    </div>
  </div>
</template>

<style scoped>
.sound-list {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  max-height: 24rem;
  overflow-y: auto;
}
</style>
