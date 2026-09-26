<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { getLogicExport, getStorage, listLibrary, songRequest, songScore, subscribe } from './api'
import AdvancedParameters from './components/AdvancedParameters.vue'
import GenerateForm from './components/GenerateForm.vue'
import LibraryList from './components/LibraryList.vue'
import NotificationButton from './components/NotificationButton.vue'
import QueueList from './components/QueueList.vue'
import PlayerBar from './components/PlayerBar.vue'
import PlaylistView from './components/PlaylistView.vue'
import TranscribePanel from './components/TranscribePanel.vue'
import { fromSongRequest, loadFormState, saveFormState } from './form'
import { formatBytes, locale, setLocale, t, workerLabel, type MessageKey } from './i18n'
import { current, retitle } from './player'
import { loadPlaylist, playlistIds } from './playlist'
import { setRatings } from './ratings'
import { navigate, view, type View } from './view'
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
/** The API's complaints about single fields, shown by the song's fields and the advanced parameters alike. */
const fieldErrors = ref<Record<string, string[]>>({})

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
    // The stream (re)opened: whatever was written meanwhile is in the library now, the playlist may have changed on
    // another device.
    void loadLibrary()
    loadPlaylist().catch((caught) => show(caught instanceof Error ? caught.message : String(caught), true))
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

// ---- Pages -------------------------------------------------------------------------------------------

const pages: { view: View; label: MessageKey; icon: string }[] = [
  { view: 'create', label: 'menuCreate', icon: 'pi pi-sparkles' },
  { view: 'transcribe', label: 'menuTranscribe', icon: 'pi pi-microphone' },
  { view: 'songs', label: 'menuSongs', icon: 'pi pi-list' },
  { view: 'playlist', label: 'menuPlaylist', icon: 'pi pi-play-circle' },
]

/**
 * The badge counts what the page holds that is worth a look: songs and transcriptions in the works, songs in the
 * playlist.
 */
const badges = computed<Partial<Record<View, number>>>(() => ({
  create: busyIds.value.size,
  transcribe: transcriptions.value.filter((tr) => !tr.finished).length,
  playlist: playlistIds.value.length,
}))

const menu = computed(() =>
  pages.map((page) => ({
    key: page.view,
    label: t(page.label),
    icon: page.icon,
    badge: badges.value[page.view] || undefined,
    command: () => navigate(page.view),
  })),
)

// ---- Library ----------------------------------------------------------------------------------------

const runs = ref<RunInfo[]>([])
const libraryLoading = ref(false)
const libraryError = ref<string | null>(null)
/** Every listed song with its run, for what the player, playlist and queue know only by id. */
const librarySongs = computed(
  () => new Map(runs.value.flatMap((run) => run.songs.map((song) => [song.id, { run, song }] as const))),
)
const listedIds = computed(() => new Set(librarySongs.value.keys()))
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
    retitle(library)
    setRatings(library)
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

const generateForm = useTemplateRef<InstanceType<typeof GenerateForm>>('generateForm')
const queueList = useTemplateRef<InstanceType<typeof QueueList>>('queueList')
function useTemplate(run: RunInfo): void {
  form.value = { ...form.value, title: run.title, style: run.style, lyrics: run.lyrics }
  navigate('create')
  show(t('templateLoaded', { title: run.title || t('untitled') }))
}

/** A transcribed melody as the score of the next song: SheetSage2 writes it without chords, for planning "melody". */
function useScore(abc: string, name: string): void {
  form.value = { ...form.value, abc, cot: 'melody' }
  navigate('create')
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
  navigate('create')
  show(
    t('songScoreApplied', {
      title: run.title || t('untitled'),
      song: t('songN', { n: song.index }),
      planning: t(cot === 'full' ? 'cotFull' : 'cotMelody'),
    }),
  )
}

/** A song the player or playlist names by id; one deleted meanwhile is gone from the library as well. */
function listedSong(songId: string): { run: RunInfo; song: SongInfo } | null {
  const found = librarySongs.value.get(songId)
  if (!found) {
    show(t('songGone'), true)
  }
  return found ?? null
}

async function useSongScoreById(songId: string): Promise<void> {
  const found = listedSong(songId)
  if (!found) {
    return
  }
  try {
    useSongScore(found.run, found.song, await songScore(songId))
  } catch (caught) {
    show(caught instanceof Error ? caught.message : String(caught), true)
  }
}

/** The song made again: everything it was generated with, from its request.json, the seed and a score included. */
async function useAsNewSong(songId: string): Promise<void> {
  const found = listedSong(songId)
  if (!found) {
    return
  }
  try {
    form.value = fromSongRequest(form.value, await songRequest(songId))
    fieldErrors.value = {}
    navigate('create')
    show(t('newSongApplied', { title: form.value.title || t('untitled'), song: t('songN', { n: found.song.index }) }))
  } catch (caught) {
    show(caught instanceof Error ? caught.message : String(caught), true)
  }
}
</script>

<template>
  <ConfirmDialog :style="{ width: 'min(28rem, calc(100vw - 2rem))' }" />
  <Menubar :model="menu" breakpoint="640px" class="mb-4" :pt="{ button: { 'aria-label': t('menu') } }">
    <template #start>
      <span class="brand">YuE UI</span>
    </template>
    <template #item="{ item, props }">
      <a
        v-bind="props.action"
        :class="['flex items-center gap-2', { 'text-primary font-semibold': item.key === view }]"
      >
        <span :class="item.icon" />
        <span>{{ item.label }}</span>
        <Badge v-if="item.badge" :value="item.badge" size="small" />
      </a>
    </template>
    <template #end>
      <div class="flex items-center gap-2">
        <span :class="['pill', worker.busy ? 'busy' : worker.status]">{{
          workerLabel(worker.status, worker.busy)
        }}</span>
        <NotificationButton @notice="show" />
        <button type="button" class="link" @click="setLocale(locale === 'de' ? 'en' : 'de')">
          {{ t('language') }}
        </button>
      </div>
    </template>
  </Menubar>

  <div class="banners">
    <p v-if="!connected" class="banner warning">{{ t('disconnected') }}</p>
    <p v-if="worker.studioRunning" class="banner warning">{{ t('studioRunning') }}</p>
    <p v-if="worker.lastError" class="banner danger">{{ t('workerError', { message: worker.lastError }) }}</p>
    <p v-if="notice" :class="['banner', notice.error ? 'danger' : 'info']" role="status">{{ notice.text }}</p>
  </div>

  <!--
    v-show rather than v-if: a page keeps what was typed or uploaded on it while another one is open.
    On wide screens the song's fields are the left column; the right one holds the advanced parameters, collapsed since
    a normal song needs none of them, and the queue below. On a phone everything is one column in that order.
  -->
  <main v-show="view === 'create'" class="grid gap-4 grid-cols-1 md:grid-cols-2 items-start">
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
          v-model:errors="fieldErrors"
          :busy="worker.busy"
          :lyrics-draft="lyricsDraft"
        />
      </template>
    </Card>

    <div class="flex min-w-0 flex-col gap-4">
      <AdvancedParameters v-model="form" v-model:errors="fieldErrors" :extensions="worker.extensions" />

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
            :listed="listedIds"
            :worker="worker"
            :log="log"
            @hide-finished="hideFinished"
            @error="show($event, true)"
          />
        </template>
      </Card>
    </div>
  </main>

  <main v-show="view === 'transcribe'">
    <Card>
      <template #title>
        <h2>{{ t('transcription') }}</h2>
      </template>
      <template #content>
        <TranscribePanel :transcriptions="transcriptions" @use-score="useScore" @error="show($event, true)" />
      </template>
    </Card>
  </main>

  <main v-show="view === 'songs'">
    <Card>
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

  <main v-show="view === 'playlist'">
    <Card>
      <template #title>
        <h2>{{ t('playlist') }}</h2>
      </template>
      <template #content>
        <PlaylistView :runs="runs" @use-score="useSongScoreById" @new-song="useAsNewSong" @error="show($event, true)" />
      </template>
    </Card>
  </main>

  <!-- Outside the pages, so switching between them does not stop the song. The spacer keeps it off the page's end. -->
  <div v-if="current" class="h-28" />
  <PlayerBar
    :has-score="!!current && !!librarySongs.get(current.id)?.song.hasScore"
    @use-score="useSongScoreById"
    @new-song="useAsNewSong"
    @error="show($event, true)"
  />
</template>

<style scoped>
.brand {
  margin-right: 0.75rem;
  font-size: 1.15rem;
  font-weight: 700;
  letter-spacing: -0.01em;
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
