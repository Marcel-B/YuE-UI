<script setup lang="ts">
import { onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { addVoice, deleteVoice, listVoices, voiceAudioUrl } from '../api'
import { formatDateTime, formatDuration, t } from '../i18n'
import type { ReferenceVoice, VersionState } from '../types'
import { showSong } from '../view'

/**
 * The reference voices ChangeMyVoice keeps, the ones a song can be sung with ("Sing with a voice" in the library),
 * and the versions in the works. The voices live only in the service, as in yue-to-logic-pro, so both apps share one
 * collection.
 */
const props = defineProps<{
  /** Shown only when a page is open, so the service is not asked while nobody looks. */
  active: boolean
  /** Versions in the works and those finished since the page loaded. */
  versions: VersionState[]
}>()

const emit = defineEmits<{ error: [message: string] }>()

const voices = ref<ReferenceVoice[] | null>(null)
const loading = ref(false)
const loadError = ref<string | null>(null)

async function load(): Promise<void> {
  loading.value = true
  try {
    voices.value = await listVoices()
    loadError.value = null
  } catch (caught) {
    loadError.value = message(caught)
  } finally {
    loading.value = false
  }
}

watch(
  () => props.active,
  (active) => {
    if (active && !loading.value) {
      void load()
    }
  },
  { immediate: true },
)

// ---- Upload ----------------------------------------------------------------------------------------

const fileInput = useTemplateRef<HTMLInputElement>('fileInput')
const label = ref('')
const file = ref<File | null>(null)
const adding = ref(false)

function chosen(event: Event): void {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
  // The file's name is a good start for the voice's name.
  if (file.value && !label.value.trim()) {
    label.value = file.value.name.replace(/\.[^.]+$/, '')
  }
}

async function add(): Promise<void> {
  if (!label.value.trim() || !file.value || adding.value) {
    return
  }
  adding.value = true
  try {
    await addVoice(label.value.trim(), file.value)
    label.value = ''
    file.value = null
    if (fileInput.value) {
      fileInput.value.value = ''
    }
    await load()
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    adding.value = false
  }
}

/** Refused (409) while a job of the service still waits for the voice. */
async function remove(voice: ReferenceVoice): Promise<void> {
  if (!window.confirm(t('voiceDeleteConfirm', { label: voice.label }))) {
    return
  }
  try {
    stop()
    await deleteVoice(voice.id)
    await load()
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Listening -------------------------------------------------------------------------------------

/**
 * One audio element of its own for the recordings, apart from the song player: a reference is a short check of
 * what the model hears, not something to queue.
 */
const audio = new Audio()
audio.preload = 'none'
const listening = ref<string | null>(null)
audio.addEventListener('ended', () => (listening.value = null))
audio.addEventListener('pause', () => (listening.value = null))
onBeforeUnmount(stop)

function listen(voice: ReferenceVoice): void {
  if (listening.value === voice.id) {
    stop()
    return
  }
  audio.src = voiceAudioUrl(voice.id)
  // Inside the click, as iOS wants it.
  void audio.play().catch(() => (listening.value = null))
  listening.value = voice.id
}

function stop(): void {
  audio.pause()
  listening.value = null
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}

const stageSeverity: Record<string, string | undefined> = {
  done: 'success',
  failed: 'danger',
  cancelled: 'secondary',
}
</script>

<template>
  <section class="flex flex-col gap-4">
    <p class="muted m-0 text-sm">{{ t('voicesIntro') }}</p>

    <div v-if="versions.length > 0">
      <h3 class="mt-0 mb-2 text-base">{{ t('versionsInWork') }}</h3>
      <ul class="m-0 flex list-none flex-col gap-2 p-0">
        <li v-for="version in versions" :key="version.id" class="flex flex-wrap items-center gap-x-3 gap-y-1">
          <a
            :href="`#/songs/${version.songId}`"
            class="text-primary no-underline"
            @click.prevent="showSong(version.songId)"
            >{{ version.title || t('untitled') }}</a
          >
          <span class="muted text-sm">{{ version.voiceLabel }}</span>
          <Tag :severity="stageSeverity[version.stage]" class="ml-auto">
            {{ t(`versionStage_${version.stage}`) }}
            <template v-if="version.stage === 'converting' && version.fraction > 0">
              {{ Math.round(version.fraction * 100) }} %
            </template>
          </Tag>
          <span v-if="version.stage === 'failed' && version.message" class="danger basis-full text-sm">{{
            version.message
          }}</span>
        </li>
      </ul>
    </div>

    <h3 v-if="versions.length > 0" class="m-0 text-base">{{ t('voiceCollection') }}</h3>
    <p v-if="loadError" class="danger m-0">{{ t('voicesError', { message: loadError }) }}</p>
    <p v-else-if="voices && voices.length === 0" class="muted m-0">{{ t('voicesEmpty') }}</p>

    <ul v-if="voices && voices.length > 0" class="m-0 flex list-none flex-col gap-1 p-0">
      <li v-for="voice in voices" :key="voice.id" class="flex items-center gap-3">
        <Button
          :icon="listening === voice.id ? 'pi pi-pause' : 'pi pi-play'"
          rounded
          outlined
          size="small"
          :aria-label="listening === voice.id ? t('pause') : t('voiceListen')"
          @click="listen(voice)"
        />
        <div class="min-w-0 flex-1">
          <div class="truncate font-semibold">{{ voice.label }}</div>
          <div class="muted text-sm">
            {{ formatDuration(voice.seconds)
            }}<template v-if="voice.createdAt"> · {{ formatDateTime(voice.createdAt) }}</template>
          </div>
        </div>
        <Button
          icon="pi pi-trash"
          text
          rounded
          size="small"
          severity="danger"
          v-tooltip="t('voiceDelete')"
          :aria-label="t('voiceDelete')"
          @click="remove(voice)"
        />
      </li>
    </ul>

    <form class="flex flex-wrap items-end gap-3 border-t border-surface pt-4" @submit.prevent="add">
      <div class="flex min-w-0 flex-[1_1_12rem] flex-col gap-1">
        <label for="voice-name" class="muted text-sm">{{ t('voiceName') }}</label>
        <InputText id="voice-name" v-model="label" required spellcheck="false" :disabled="adding" fluid />
      </div>
      <div class="flex min-w-0 flex-[1_1_16rem] flex-col gap-1">
        <label for="voice-file" class="muted text-sm">{{ t('voiceFile') }}</label>
        <!-- A native file input: PrimeVue's FileUpload brings its own upload flow, while this only picks a file. -->
        <input
          id="voice-file"
          ref="fileInput"
          class="max-w-full text-sm"
          type="file"
          accept="audio/*,.wav,.mp3,.flac,.m4a,.aac,.ogg,.opus"
          required
          :disabled="adding"
          @change="chosen"
        />
      </div>
      <Button
        type="submit"
        :label="adding ? t('voiceAdding') : t('voiceAdd')"
        icon="pi pi-upload"
        :loading="adding"
        :disabled="adding || !label.trim() || !file"
      />
    </form>
    <p class="muted m-0 text-sm">{{ t('voiceFileHint') }}</p>
  </section>
</template>
