<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, ref, useTemplateRef, watch } from 'vue'
import { getStorage, getVoiceInfo, listLibrary, songRequest, songScore, subscribe } from './api'
import AdvancedParameters from './components/AdvancedParameters.vue'
import GenerateForm from './components/GenerateForm.vue'
import NotificationButton from './components/NotificationButton.vue'
import QueueList from './components/QueueList.vue'
import AudioBackground from './components/AudioBackground.vue'
import PlayerBar from './components/PlayerBar.vue'
import PlaylistView from './components/PlaylistView.vue'
import TranscribePanel from './components/TranscribePanel.vue'
import VoicesPanel from './components/VoicesPanel.vue'
import StemsPanel from './components/StemsPanel.vue'
import Message from 'primevue/message'
import { fromSongRequest, loadFormState, planningFor, saveFormState } from './form'
import { formatBytes, locale, setLocale, t, workerLabel, type MessageKey } from './i18n'
import { current, retitle } from './player'
import { loadPlaylists, playlistIds } from './playlist'
import { setRatings } from './ratings'
import { shareState } from './share'
import { checkForUpdate, reload, standalone, updateAvailable } from './update'
import { visuals } from './spectrum'
import { navigate, view, type View } from './view'
import type {
  LogEntry,
  LyricsState,
  RunInfo,
  SongInfo,
  SongState,
  SpeechTake,
  StorageInfo,
  TranscriptionState,
  StemSetState,
  VersionState,
  QueuedJob,
  VoiceInfo,
  WorkerInfo,
} from './types'

// Loaded after the first paint: the library brings DataView with its Paginator and InputNumber, Fieldset and
// Dialog, a good part of PrimeVue that the create page does not need; sharing needs its dialog only when used.
const LibraryList = defineAsyncComponent(() => import('./components/LibraryList.vue'))
const ShareDialog = defineAsyncComponent(() => import('./components/ShareDialog.vue'))
// The Logic page brings the options form, the piano roll and the MIDI preview; loaded only once it is opened.
const LogicPage = defineAsyncComponent(() => import('./components/LogicPage.vue'))
// The speech lab, likewise loaded and mounted only once it is opened.
const SpeechLab = defineAsyncComponent(() => import('./components/SpeechLab.vue'))

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
/** Green once the model is loaded; the accent while it starts or works; grey while no worker runs. */
const workerSeverity = computed(() =>
  worker.value.busy || worker.value.status === 'starting'
    ? undefined
    : worker.value.status === 'ready'
      ? 'success'
      : 'secondary',
)
const songs = ref<SongState[]>([])
const log = ref<LogEntry[]>([])
const transcriptions = ref<TranscriptionState[]>([])
const lyricsDraft = ref<LyricsState | null>(null)
/** Versions in the works, and those finished while the page was open; by id. */
const versions = ref<VersionState[]>([])
/** Stems in the works, and those finished while the page was open. */
const stemSets = ref<StemSetState[]>([])
/** The speech lab's takes as the event stream reports them, by id; the lab lays them over what it loaded. */
const speechTakes = ref<SpeechTake[]>([])
/** Whether a take holds the memory, so the queue can say what its jobs wait for. */
const speaking = computed(() => speechTakes.value.some((take) => take.stage === 'loading' || take.stage === 'speaking'))
/** Songs, renders and drafts waiting for the memory. */
const jobs = ref<QueuedJob[]>([])
/** How long songs may pass a waiting draft or version (Queue:BundleWindow). */
const bundleWindowSeconds = ref<number | null>(null)
/** Lyrics drafts among them, so that the form knows its own is still on its way. */
const queuedIds = computed(() => new Set(jobs.value.map((j) => j.id)))
/** Starts optimistic: the warning is for a stream that broke, not for one that is still opening. */
const connected = ref(true)
/** Finished songs the user put away; the server keeps them, so they would come back with the next snapshot. */
const hidden = ref(new Set<string>())

const queue = computed(() => songs.value.filter((s) => !(s.finished && hidden.value.has(s.id))))
// Kept as the same Set while the busy songs stay the same: a progress event replaces a song, and a new Set would
// re-render the whole library with every one.
const busyIds = computed<Set<string>>((previous) => {
  // A render waiting in the queue counts too: the library offers it once, not a second time.
  const next = new Set([
    ...songs.value.filter((s) => !s.finished).map((s) => s.id),
    ...jobs.value.flatMap((j) => (j.songId ? [j.songId] : [])),
  ])
  return previous && previous.size === next.size && [...next].every((id) => previous.has(id)) ? previous : next
})

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

function upsertVersion(version: VersionState): void {
  const index = versions.value.findIndex((v) => v.id === version.id)
  if (index >= 0) {
    versions.value[index] = version
  } else {
    versions.value.push(version)
  }
}

function upsertStems(set: StemSetState): void {
  const index = stemSets.value.findIndex((s) => s.id === set.id)
  if (index >= 0) {
    stemSets.value[index] = set
  } else {
    stemSets.value.push(set)
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
    versions.value = snapshot.versions
    stemSets.value = snapshot.stems ?? []
    jobs.value = snapshot.queue ?? []
    bundleWindowSeconds.value = snapshot.bundleWindowSeconds ?? null
    // The stream (re)opened: whatever was written meanwhile is in the library now, the playlists may have changed on
    // another device.
    void loadLibrary()
    // A deploy restarts the server, so a reopened stream is also when a new build may be there.
    void checkForUpdate()
    loadPlaylists().catch((caught) => show(caught instanceof Error ? caught.message : String(caught), true))
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
  version: upsertVersion,
  stems: upsertStems,
  speech(take) {
    const index = speechTakes.value.findIndex((other) => other.id === take.id)
    if (index >= 0) {
      speechTakes.value[index] = take
    } else {
      speechTakes.value.push(take)
    }
  },
  queue(queue) {
    jobs.value = queue
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
  { view: 'voices', label: 'menuVoices', icon: 'pi pi-users' },
  { view: 'logic', label: 'menuLogic', icon: 'pi pi-box' },
  { view: 'lab', label: 'menuLab', icon: 'pi pi-comments' },
]

/**
 * The badge counts what the page holds that is worth a look: songs and transcriptions in the works or waiting, songs
 * in the playlist.
 */
const badges = computed<Partial<Record<View, number>>>(() => ({
  create:
    songs.value.filter((s) => !s.finished).length +
    jobs.value.length +
    versions.value.filter((v) => !v.finished).length +
    stemSets.value.filter((s) => !s.finished).length,
  transcribe: transcriptions.value.filter((tr) => !tr.finished).length,
  playlist: playlistIds.value.length,
  voices: versions.value.filter((v) => !v.finished).length + stemSets.value.filter((s) => !s.finished).length,
  lab: speechTakes.value.filter((take) => !take.finished).length,
}))

/**
 * Voices need ChangeMyVoice, stems StemMyWav; without either the page is left out of the menu, and the library offers
 * only what is there.
 */
const voiceInfo = ref<VoiceInfo>({ voicesConfigured: false, conversionConfigured: false, stemsConfigured: false })
const voicesPage = computed(() => voiceInfo.value.voicesConfigured || voiceInfo.value.stemsConfigured)
getVoiceInfo()
  .then((info) => (voiceInfo.value = info))
  .catch(() => undefined)

/**
 * The Logic page is only built once it is opened, and then kept like the others: it asks the server for instruments
 * and presets on its first appearance, which a visit to the other pages does not need.
 */
const logicOpened = ref(view.value === 'logic')
const labOpened = ref(view.value === 'lab')
watch(view, (value) => {
  if (value === 'logic') {
    logicOpened.value = true
  }
  if (value === 'lab') {
    labOpened.value = true
  }
})

const menu = computed(() =>
  pages
    .filter((page) => page.view !== 'voices' || voicesPage.value)
    .map((page) => ({
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
 */
function useSongScore(run: RunInfo, song: SongInfo, abc: string, midiWarnings?: string[]): void {
  const cot = planningFor(abc)
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
  const applied = t(midiWarnings ? 'songMidiApplied' : 'songScoreApplied', {
    title: run.title || t('untitled'),
    song: t('songN', { n: song.index }),
    planning: t(cot === 'full' ? 'cotFull' : 'cotMelody'),
  })
  show(midiWarnings?.length ? `${applied} ${t('midiWarnings', { messages: midiWarnings.join(' ') })}` : applied)
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
  <AudioBackground v-if="visuals.background" />
  <ConfirmDialog :style="{ width: 'min(28rem, calc(100vw - 2rem))' }" />
  <ShareDialog v-if="shareState" />
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
        <Tag
          :value="workerLabel(worker.status, worker.busy)"
          :severity="workerSeverity"
          rounded
          class="whitespace-nowrap"
        />
        <NotificationButton @notice="show" />
        <Button
          v-if="standalone"
          icon="pi pi-refresh"
          severity="secondary"
          text
          size="small"
          :aria-label="t('reload')"
          v-tooltip.bottom="t('reload')"
          @click="reload"
        />
        <Button :label="t('language')" text size="small" @click="setLocale(locale === 'de' ? 'en' : 'de')" />
      </div>
    </template>
  </Menubar>

  <div class="mb-4 flex flex-col gap-2 empty:hidden">
    <Message v-if="!connected" severity="warn">{{ t('disconnected') }}</Message>
    <Message v-if="updateAvailable" severity="info">
      <div class="flex flex-wrap items-center gap-x-3 gap-y-1">
        <span>{{ t('updateAvailable') }}</span>
        <Button :label="t('reload')" icon="pi pi-refresh" size="small" @click="reload" />
      </div>
    </Message>
    <Message v-if="worker.studioRunning" severity="warn">{{ t('studioRunning') }}</Message>
    <Message v-if="worker.lastError" severity="error">{{ t('workerError', { message: worker.lastError }) }}</Message>
    <Message v-if="notice" :severity="notice.error ? 'error' : 'info'" role="status">{{ notice.text }}</Message>
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
          :queued-ids="queuedIds"
          :lyrics-draft="lyricsDraft"
          :voices="voiceInfo.conversionConfigured"
        />
      </template>
    </Card>

    <div class="flex min-w-0 flex-col gap-4">
      <AdvancedParameters
        v-model="form"
        v-model:errors="fieldErrors"
        :extensions="worker.extensions"
        @notice="show($event)"
        @error="show($event, true)"
      />

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
            :jobs="jobs"
            :listed="listedIds"
            :worker="worker"
            :lyrics-draft="lyricsDraft"
            :versions="versions"
            :stems="stemSets"
            :bundle-window-seconds="bundleWindowSeconds"
            :speaking="speaking"
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
          :voices="voiceInfo.conversionConfigured"
          :stems="voiceInfo.stemsConfigured"
          :live-versions="versions"
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

  <main v-if="voicesPage" v-show="view === 'voices'" class="flex flex-col gap-4">
    <Card v-if="voiceInfo.voicesConfigured">
      <template #title>
        <h2>{{ t('voices') }}</h2>
      </template>
      <template #content>
        <VoicesPanel :active="view === 'voices'" :versions="versions" @error="show($event, true)" />
      </template>
    </Card>
    <Card v-if="voiceInfo.stemsConfigured">
      <template #title>
        <h2>{{ t('stems') }}</h2>
      </template>
      <template #content>
        <StemsPanel :active="view === 'voices'" :runs="runs" :live="stemSets" @error="show($event, true)" />
      </template>
    </Card>
  </main>

  <main v-if="logicOpened" v-show="view === 'logic'">
    <LogicPage :runs="runs" />
  </main>

  <main v-if="labOpened" v-show="view === 'lab'">
    <SpeechLab :active="view === 'lab'" :connected="connected" :live="speechTakes" @error="show($event, true)" />
  </main>

  <!-- Outside the pages, so switching between them does not stop the song. The spacer keeps it off the page's end. -->
  <div v-if="current" class="h-28" />
  <PlayerBar
    :has-score="!!current && !!librarySongs.get(current.songId)?.song.hasScore"
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
</style>
