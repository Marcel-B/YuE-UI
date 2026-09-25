<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { getLogicExport, getStorage, listLibrary, subscribe } from './api'
import GenerateForm from './components/GenerateForm.vue'
import LibraryList from './components/LibraryList.vue'
import QueueList from './components/QueueList.vue'
import TranscribePanel from './components/TranscribePanel.vue'
import { loadFormState, saveFormState } from './form'
import { formatBytes, locale, setLocale, t, workerLabel } from './i18n'
import type {
  LogEntry,
  LyricsState,
  RunInfo,
  SongInfo,
  SongState,
  StorageInfo,
  TranscriptionState,
  WorkerInfo,
} from './types'

const logCapacity = 300
const form = ref(loadFormState())
watch(form, (value) => saveFormState(value), { deep: true })

// ---- Live state of the worker, from the event stream ----------------------------------------------

const worker = ref<WorkerInfo>({
  status: 'stopped',
  busy: false,
  studioRunning: false,
  lastError: null,
  extensions: null,
})
const songs = ref<SongState[]>([])
const log = ref<LogEntry[]>([])
const transcriptions = ref<TranscriptionState[]>([])
const lyricsDraft = ref<LyricsState | null>(null)
/** Starts optimistic: the warning is for a stream that broke, not for one that is still opening. */
const connected = ref(true)
/** Finished songs the user put away; the server keeps them, so they would come back with the next snapshot. */
const hidden = ref(new Set<string>())

const queue = computed(() => songs.value.filter((s) => !(s.finished && hidden.value.has(s.id))))
const busyIds = computed(() => new Set(songs.value.filter((s) => !s.finished).map((s) => s.id)))

function upsert(song: SongState): void {
  const index = songs.value.findIndex((s) => s.id === song.id)
  if (index >= 0) {
    songs.value[index] = song
  } else {
    songs.value.push(song)
    songs.value.sort((a, b) => a.run.localeCompare(b.run) || a.index - b.index)
  }
  // A song rendered again after being hidden is news again.
  if (!song.finished) {
    hidden.value.delete(song.id)
  }
}

function hideFinished(): void {
  hidden.value = new Set([...hidden.value, ...songs.value.filter((s) => s.finished).map((s) => s.id)])
}

const unsubscribe = subscribe({
  snapshot(snapshot) {
    worker.value = snapshot.worker
    songs.value = snapshot.songs
    log.value = snapshot.log
    transcriptions.value = snapshot.transcriptions
    lyricsDraft.value = snapshot.lyrics
    // The stream (re)opened: whatever was written meanwhile is in the library now.
    void loadLibrary()
  },
  song: upsert,
  worker(info) {
    worker.value = info
  },
  log(entry) {
    log.value.push(entry)
    if (log.value.length > logCapacity) {
      log.value.splice(0, log.value.length - logCapacity)
    }
  },
  library: () => scheduleLibraryReload(),
  transcription(transcription) {
    const index = transcriptions.value.findIndex((tr) => tr.id === transcription.id)
    if (index >= 0) {
      transcriptions.value[index] = transcription
    } else {
      transcriptions.value.push(transcription)
    }
  },
  lyrics(lyrics) {
    lyricsDraft.value = lyrics
  },
  connection(open) {
    connected.value = open
  },
})
onBeforeUnmount(unsubscribe)

// ---- Library ----------------------------------------------------------------------------------------

const runs = ref<RunInfo[]>([])
const libraryLoading = ref(false)
const libraryError = ref<string | null>(null)
const storage = ref<StorageInfo | null>(null)
/** What all runs take, so it is clear what deleting would gain. */
const storageLabel = computed(() =>
  storage.value
    ? t('storage', {
        used: formatBytes(runs.value.reduce((sum, run) => sum + run.bytes, 0)),
        free: formatBytes(storage.value.freeBytes),
      })
    : '',
)

/** Songs become Logic projects only when this server knows a yue-to-logic-pro; the library hides the button otherwise. */
const logicExport = ref(false)
getLogicExport()
  .then((info) => (logicExport.value = info.configured))
  .catch(() => (logicExport.value = false))

async function loadLibrary(): Promise<void> {
  libraryLoading.value = true
  try {
    // The free space is a hint only; the library shows without it.
    const [library, space] = await Promise.all([listLibrary(), getStorage().catch(() => null)])
    runs.value = library
    storage.value = space
    libraryError.value = null
  } catch (caught) {
    libraryError.value = caught instanceof Error ? caught.message : String(caught)
  } finally {
    libraryLoading.value = false
  }
}

/** The server tells every browser to reload as well; this one should not wait for that. */
function onDeleted(title: string): void {
  show(t('deleted', { title }))
  void loadLibrary()
}

/** A batch finishes its songs in quick succession; read the folder once for all of them. */
let libraryTimer: ReturnType<typeof setTimeout> | undefined
function scheduleLibraryReload(): void {
  clearTimeout(libraryTimer)
  libraryTimer = setTimeout(() => void loadLibrary(), 500)
}

// ---- Messages and templates ---------------------------------------------------------------------

const notice = ref<{ text: string; error: boolean } | null>(null)
let noticeTimer: ReturnType<typeof setTimeout> | undefined
function show(text: string, error = false): void {
  notice.value = { text, error }
  clearTimeout(noticeTimer)
  noticeTimer = setTimeout(() => (notice.value = null), 6000)
}

const formSection = useTemplateRef<HTMLElement>('formSection')
const generateForm = useTemplateRef<InstanceType<typeof GenerateForm>>('generateForm')
const queueList = useTemplateRef<InstanceType<typeof QueueList>>('queueList')
function useTemplate(run: RunInfo): void {
  form.value = { ...form.value, title: run.title, style: run.style, lyrics: run.lyrics }
  formSection.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  show(t('templateLoaded', { title: run.title || t('untitled') }))
}

/** A transcribed melody as the score of the next song: SheetSage2 writes it without chords, for planning "melody". */
function useScore(abc: string, name: string): void {
  form.value = { ...form.value, abc, cot: 'melody' }
  formSection.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  show(t('scoreApplied', { name }))
}

/**
 * A song's score to build on, e.g. with changed chords or tempo: with its run's style, lyrics and the song's seed,
 * so that a changed score is all that differs. Planning follows the score: chords are kept when it has them.
 * Only body lines count, the voice declarations quote their names as well.
 */
function useSongScore(run: RunInfo, song: SongInfo, abc: string): void {
  const cot = /^(?![A-Za-z]:|%).*"[^"]+"/m.test(abc) ? 'full' : 'melody'
  form.value = {
    ...form.value,
    title: run.title,
    style: run.style,
    lyrics: run.lyrics,
    abc,
    cot,
    seed: song.seed === null ? form.value.seed : String(song.seed),
  }
  formSection.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
  show(
    t('songScoreApplied', {
      title: run.title || t('untitled'),
      song: t('songN', { n: song.index }),
      planning: t(cot === 'full' ? 'cotFull' : 'cotMelody'),
    }),
  )
}
</script>

<template>
  <ConfirmDialog :style="{ width: 'min(28rem, calc(100vw - 2rem))' }" />
  <header class="top">
    <div>
      <h1>YuE UI</h1>
      <p class="muted">{{ t('subtitle') }}</p>
    </div>
    <div class="status">
      <span :class="['pill', worker.busy ? 'busy' : worker.status]">{{ workerLabel(worker.status, worker.busy) }}</span>
      <button type="button" class="link" @click="setLocale(locale === 'de' ? 'en' : 'de')">{{ t('language') }}</button>
    </div>
  </header>

  <div class="banners">
    <p v-if="!connected" class="banner warning">{{ t('disconnected') }}</p>
    <p v-if="worker.studioRunning" class="banner warning">{{ t('studioRunning') }}</p>
    <p v-if="worker.lastError" class="banner danger">{{ t('workerError', { message: worker.lastError }) }}</p>
    <p v-if="notice" :class="['banner', notice.error ? 'danger' : 'info']" role="status">{{ notice.text }}</p>
  </div>

  <main class="grid gap-4 grid-cols-1 md:grid-cols-2">
    <Card>
      <template #title>
        <h2>
          <div class="flex justify-between">
            <div>
              {{ t('newSong') }}
            </div>

            <Button icon="pi pi-trash" rounded text :aria-label="t('resetForm')" @click="generateForm?.reset()" />
          </div>
        </h2>
      </template>
      <template #content>
        <GenerateForm
          ref="generateForm"
          v-model="form"
          :extensions="worker.extensions"
          :busy="worker.busy"
          :lyrics-draft="lyricsDraft"
        />
      </template>
    </Card>
    <!-- <div ref="formSection"></div> -->
    <div>
      <Card>
        <template #title>
          <div class="flex flex-wrap justify-between">
            <h2>{{ t('queue') }}</h2>
            <Button
              icon="pi pi-stop-filled"
              text
              rounded
              v-if="worker.busy"
              @click="queueList?.run(queueList?.stopAll)"
            />
          </div>
        </template>
        <template #content>
          <QueueList
            ref="queueList"
            :songs="queue"
            :worker="worker"
            :log="log"
            @hide-finished="hideFinished"
            @error="show($event, true)"
          />
        </template>
      </Card>

      <Card class="mt-4">
        <template #title>
          <h2>{{ t('transcriptions') }}</h2>
        </template>
        <template #content>
          <TranscribePanel :transcriptions="transcriptions" @use-score="useScore" @error="show($event, true)" />
        </template>
      </Card>
    </div>

    <Card class="md:col-span-2">
      <template #title>
        <div class="flex justify-between items-center">
          <h2>{{ t('library') }}</h2>
          <span v-if="storageLabel" class="muted text-sm font-normal ml-auto mr-2">{{ storageLabel }}</span>
          <Button
            icon="pi pi-refresh"
            :disabled="libraryLoading"
            text
            rounded
            :aria-label="t('refresh')"
            @click="loadLibrary"
          />
        </div>
      </template>
      <template #content>
        <LibraryList
          :runs="runs"
          :loading="libraryLoading"
          :error="libraryError"
          :busy-ids="busyIds"
          :logic-export="logicExport"
          @template="useTemplate"
          @use-score="useSongScore"
          @deleted="onDeleted"
          @notice="show($event)"
          @error="show($event, true)"
        />
      </template>
    </Card>
  </main>
</template>

<style scoped>
.top {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 1rem;
  margin-bottom: 1.25rem;
}

h1 {
  margin: 0;
  font-size: 1.6rem;
  letter-spacing: -0.01em;
}

.top p {
  margin: 0;
}

.status {
  display: flex;
  flex-direction: column;
  align-items: flex-end;
  gap: 0.35rem;
}

.pill {
  padding: 0.2rem 0.7rem;
  border-radius: 999px;
  background: var(--surface-sunken);
  color: var(--text-muted);
  font-size: 0.8rem;
  font-weight: 600;
  white-space: nowrap;
}

.pill.ready {
  background: var(--success-soft);
  color: var(--success);
}

.pill.starting,
.pill.busy {
  background: var(--accent-soft);
  color: var(--accent);
}

.banners {
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
  margin-bottom: 1rem;
}

.banners:empty {
  display: none;
}

.banner {
  margin: 0;
  padding: 0.65rem 0.9rem;
  border-radius: var(--radius-small);
  font-size: 0.9rem;
}

.banner.warning {
  background: var(--warning-soft);
  color: var(--warning-text);
}

.banner.danger {
  background: var(--danger-soft);
  color: var(--danger);
}

.banner.info {
  background: var(--accent-soft);
  color: var(--accent);
}

.layout {
  display: grid;
  grid-template-columns: minmax(0, 1fr);
  gap: 1.25rem;
}

@media (min-width: 60rem) {
  .layout {
    grid-template-columns: minmax(0, 3fr) minmax(0, 2fr);
    align-items: start;
  }

  .queue-column {
    position: sticky;
    top: 1rem;
  }

  .library-row {
    grid-column: 1 / -1;
  }
}

.form-column {
  scroll-margin-top: 1rem;
}
</style>
