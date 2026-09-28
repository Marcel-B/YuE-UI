<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { deleteStems, listStemModels, listStems, separateSong, stemAudioUrl } from '../api'
import { formatDateTime, formatDuration, hasMessage, t } from '../i18n'
import { playing as songPlaying, toggle as toggleSong } from '../player'
import type { RunInfo, StemFile, StemModel, StemSetState } from '../types'
import { showSong, songHref, stemSong } from '../view'
import WaveformView from './WaveformView.vue'

/**
 * Songs split into their stems by StemMyWav: pick a song and a model, and the stems are listed below once they are
 * made, each with its waveform to listen to and download. The separation waits in the same queue as the voices,
 * since it needs the same memory.
 */
const props = defineProps<{
  /** The page is open; the lists are only asked for then. */
  active: boolean
  runs: RunInfo[]
  /** Sets in the works, and those finished since the page loaded, as the event stream reports them. */
  live: StemSetState[]
}>()

const emit = defineEmits<{ error: [message: string] }>()

const loaded = ref<StemSetState[]>([])
const loadError = ref<string | null>(null)
const models = ref<StemModel[]>([])
/** Deleted here; the server's `cancelled` event may come after the list was read again. */
const removed = ref(new Set<string>())

async function load(): Promise<void> {
  try {
    loaded.value = await listStems()
    loadError.value = null
  } catch (caught) {
    loadError.value = message(caught)
  }
}

async function loadModels(): Promise<void> {
  try {
    models.value = await listStemModels()
    if (!models.value.some((m) => m.id === model.value)) {
      model.value = models.value.find((m) => m.isDefault)?.id ?? models.value[0]?.id ?? null
    }
  } catch (caught) {
    emit('error', message(caught))
  }
}

watch(
  () => props.active,
  (active) => {
    if (active) {
      void load()
      if (models.value.length === 0) {
        void loadModels()
      }
    }
  },
  { immediate: true },
)
// A deleted song takes its stems along on the server.
watch(
  () => props.runs,
  () => {
    if (props.active) {
      void load()
    }
  },
)

/** The live state wins: it is at least as new as the list. */
const sets = computed(() => {
  const byId = new Map(loaded.value.map((set) => [set.id, set]))
  for (const set of props.live) {
    byId.set(set.id, set)
  }
  return [...byId.values()]
    .filter((set) => set.stage !== 'cancelled' && !removed.value.has(set.id))
    .sort((a, b) => b.createdAt.localeCompare(a.createdAt))
})

// ---- Asking for stems -------------------------------------------------------------------------------

const song = ref<string | null>(stemSong.value)
watch(stemSong, (value) => {
  if (value) {
    song.value = value
  }
})

const songOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((s) => s.hasAudio)
      .map((s) => ({
        value: s.id,
        label: `${run.title || t('untitled')} · ${t('songN', { n: s.index })}`,
        seconds: s.seconds,
      })),
  ),
)

const modelKey = 'yue-ui.stemModel'
const model = ref<string | null>(readModel())
watch(model, (value) => {
  try {
    if (value) {
      localStorage.setItem(modelKey, value)
    }
  } catch {
    // Only a convenience.
  }
})

function readModel(): string | null {
  try {
    return localStorage.getItem(modelKey)
  } catch {
    return null
  }
}

const chosenModel = computed(() => models.value.find((m) => m.id === model.value) ?? null)
const dereverb = ref(false)
const separating = ref(false)

/** What the model makes and roughly how long it takes for the chosen song. */
const modelHint = computed(() => {
  const chosen = chosenModel.value
  if (!chosen) {
    return ''
  }
  const parts: string[] = []
  if (chosen.stems?.length) {
    parts.push(chosen.stems.map(stemLabel).join(', '))
  }
  const seconds = songOptions.value.find((o) => o.value === song.value)?.seconds
  if (chosen.realtimeFactor && seconds) {
    parts.push(t('stemsEstimate', { duration: formatDuration(seconds / chosen.realtimeFactor) }))
  }
  return parts.join(' · ')
})

async function separate(): Promise<void> {
  if (!song.value || separating.value) {
    return
  }
  separating.value = true
  try {
    const set = await separateSong(song.value, model.value, dereverb.value)
    loaded.value = [set, ...loaded.value.filter((s) => s.id !== set.id)]
  } catch (caught) {
    emit('error', message(caught))
  } finally {
    separating.value = false
  }
}

async function remove(set: StemSetState): Promise<void> {
  const question = set.finished ? 'stemsDeleteConfirm' : 'stemsCancelConfirm'
  if (!window.confirm(t(question, { title: set.title || t('untitled'), song: songName(set) }))) {
    return
  }
  try {
    if (listening.value?.set === set.id) {
      stop()
    }
    await deleteStems(set.songId, set.id)
    removed.value = new Set([...removed.value, set.id])
  } catch (caught) {
    emit('error', message(caught))
  }
}

// ---- Listening --------------------------------------------------------------------------------------

/**
 * One audio element for the stems, apart from the song player, like the reference voices: a stem is something to
 * compare, not to queue. Switching to another stem of the same song keeps the time, so the parts can be heard one
 * after the other at the same bar.
 */
const audio = new Audio()
audio.preload = 'none'
const listening = ref<{ set: string; name: string } | null>(null)
const position = ref(0)
let frame = 0

audio.addEventListener('pause', () => cancelAnimationFrame(frame))
audio.addEventListener('ended', () => (listening.value = null))
audio.addEventListener('play', follow)
onBeforeUnmount(stop)

/** The position line moves with every frame while it plays; timeupdate comes only a few times a second. */
function follow(): void {
  position.value = audio.currentTime
  if (!audio.paused) {
    frame = requestAnimationFrame(follow)
  }
}

function isPlaying(set: StemSetState, stem: StemFile): boolean {
  return listening.value?.set === set.id && listening.value.name === stem.name && !paused.value
}

const paused = ref(true)
audio.addEventListener('pause', () => (paused.value = true))
audio.addEventListener('play', () => (paused.value = false))

function toggle(set: StemSetState, stem: StemFile): void {
  if (listening.value?.set === set.id && listening.value.name === stem.name) {
    if (audio.paused) {
      void audio.play().catch(() => undefined)
    } else {
      audio.pause()
    }
    return
  }
  const at = listening.value?.set === set.id ? audio.currentTime : 0
  start(set, stem, at)
}

/** Inside the click, as iOS wants it. */
function start(set: StemSetState, stem: StemFile, at: number): void {
  // Two songs at once help nobody.
  if (songPlaying.value) {
    toggleSong()
  }
  listening.value = { set: set.id, name: stem.name }
  audio.src = stemAudioUrl(set.songId, set.id, stem.name)
  audio.currentTime = at
  position.value = at
  void audio.play().catch(() => (listening.value = null))
}

function seek(set: StemSetState, stem: StemFile, seconds: number): void {
  if (listening.value?.set === set.id && listening.value.name === stem.name) {
    audio.currentTime = seconds
    position.value = seconds
  } else {
    start(set, stem, seconds)
  }
}

function stop(): void {
  audio.pause()
  listening.value = null
}

function stemPosition(set: StemSetState, stem: StemFile): number {
  return listening.value?.set === set.id && listening.value.name === stem.name ? position.value : 0
}

// ---- Labels -----------------------------------------------------------------------------------------

/** StemMyWav's file names, as people call them; an unknown one as it is. */
function stemLabel(name: string): string {
  const key = `stem_${name.toLowerCase()}`
  return hasMessage(key) ? t(key) : name
}

function modelName(id: string): string {
  return models.value.find((m) => m.id === id)?.name ?? id
}

function songName(set: StemSetState): string {
  return t('songN', { n: set.songId.slice(set.songId.lastIndexOf('/') + 5) })
}

function message(caught: unknown): string {
  return caught instanceof Error ? caught.message : String(caught)
}

const stageSeverity: Record<string, string | undefined> = { done: 'success', failed: 'danger' }
</script>

<template>
  <section class="flex flex-col gap-4">
    <p class="muted m-0 text-sm">{{ t('stemsIntro') }}</p>

    <form class="flex flex-wrap items-end gap-3" @submit.prevent="separate">
      <div class="flex min-w-0 flex-[1_1_16rem] flex-col gap-1">
        <label for="stems-song" class="muted text-sm">{{ t('stemsSong') }}</label>
        <Select
          v-model="song"
          input-id="stems-song"
          :options="songOptions"
          option-label="label"
          option-value="value"
          filter
          :placeholder="t('stemsSongPick')"
          :empty-message="t('stemsNoSongs')"
          fluid
        />
      </div>
      <div class="flex min-w-0 flex-[1_1_12rem] flex-col gap-1">
        <label for="stems-model" class="muted text-sm">{{ t('stemsModel') }}</label>
        <Select v-model="model" input-id="stems-model" :options="models" option-label="name" option-value="id" fluid />
      </div>
      <div class="flex items-center gap-2 py-2">
        <Checkbox v-model="dereverb" input-id="stems-dereverb" binary />
        <label for="stems-dereverb" class="text-sm">{{ t('stemsDereverb') }}</label>
      </div>
      <Button
        type="submit"
        :label="t('stemsSeparate')"
        icon="pi pi-sliders-v"
        :loading="separating"
        :disabled="!song || separating"
      />
    </form>
    <p v-if="modelHint" class="muted -mt-2 mb-0 text-sm">{{ modelHint }}</p>

    <p v-if="loadError" class="danger m-0">{{ t('stemsError', { message: loadError }) }}</p>
    <p v-else-if="sets.length === 0" class="muted m-0">{{ t('stemsEmpty') }}</p>

    <ul class="m-0 flex list-none flex-col gap-4 p-0">
      <li v-for="set in sets" :key="set.id" class="flex flex-col gap-2 border-t border-surface pt-3">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
          <div class="min-w-0 flex-1">
            <a
              :href="songHref(set.songId)"
              class="font-semibold text-primary no-underline"
              @click.prevent="showSong(set.songId)"
              >{{ set.title || t('untitled') }} · {{ songName(set) }}</a
            >
            <div class="muted truncate text-sm">
              {{ modelName(set.model) }}<template v-if="set.dereverb"> · {{ t('stemsDereverbShort') }}</template> ·
              {{ formatDateTime(set.createdAt) }}
            </div>
          </div>
          <Tag v-if="set.stage !== 'done'" :severity="stageSeverity[set.stage]">
            <i v-if="set.stage === 'separating'" class="pi pi-spin pi-spinner text-xs" />
            {{ t(`stemsStage_${set.stage}`) }}
          </Tag>
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="danger"
            v-tooltip="set.finished ? t('stemsDelete') : t('stemsCancel')"
            :aria-label="set.finished ? t('stemsDelete') : t('stemsCancel')"
            @click="remove(set)"
          />
        </div>
        <p v-if="set.stage === 'failed' && set.message" class="danger m-0 text-sm">{{ set.message }}</p>

        <div v-for="stem in set.stems" :key="stem.name" class="flex items-center gap-2">
          <Button
            :icon="isPlaying(set, stem) ? 'pi pi-pause' : 'pi pi-play'"
            rounded
            outlined
            size="small"
            :aria-label="isPlaying(set, stem) ? t('pause') : t('stemsListen', { stem: stemLabel(stem.name) })"
            @click="toggle(set, stem)"
          />
          <div class="min-w-0 flex-1">
            <div class="flex items-baseline gap-2 text-sm">
              <span class="truncate font-semibold">{{ stemLabel(stem.name) }}</span>
              <span v-if="stem.seconds > 0" class="muted ml-auto shrink-0 tabular-nums">
                <template v-if="listening?.set === set.id && listening.name === stem.name"
                  >{{ formatDuration(position) }} /
                </template>
                {{ formatDuration(stem.seconds) }}
              </span>
            </div>
            <WaveformView
              v-if="stem.peaks && stem.seconds > 0"
              :peaks="stem.peaks"
              :duration="stem.seconds"
              :start="0"
              :end="stemPosition(set, stem)"
              :position="stemPosition(set, stem)"
              compact
              @seek="seek(set, stem, $event)"
            />
          </div>
          <Button
            as="a"
            :href="stemAudioUrl(set.songId, set.id, stem.name, true)"
            icon="pi pi-download"
            text
            rounded
            size="small"
            v-tooltip="t('stemsDownload')"
            :aria-label="t('stemsDownload')"
          />
        </div>
      </li>
    </ul>
  </section>
</template>
