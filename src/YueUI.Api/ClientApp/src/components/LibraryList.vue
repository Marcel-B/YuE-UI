<script setup lang="ts">
import { computed, nextTick, ref, useTemplateRef, watch } from 'vue'
import DataView from 'primevue/dataview'
import Dialog from 'primevue/dialog'
import Checkbox from 'primevue/checkbox'
import Fieldset from 'primevue/fieldset'
import type Menu from 'primevue/menu'
import { useConfirm } from 'primevue/useconfirm'
import {
  addVersion,
  audioUrl,
  deleteVersion,
  deleteRun,
  deleteSong,
  listVoices,
  midiToAbc,
  renameRun,
  render,
  runZipUrl,
  scoreUrl,
  songScore,
  songZipUrl,
  versionAudioUrl,
} from '../api'
import { formatBytes, formatDateTime, formatDuration, t, versionProgress } from '../i18n'
import { current, libraryTracks, play, playing, trackOf, versionTrack } from '../player'
import { pickMidiFile } from '../midi'
import { rate, ratingOf, ratings } from '../ratings'
import { shareSong } from '../share'
import { openExport } from '../export'
import { matchesRun, matchingLines, parseQuery, type SearchScope } from '../search'
import { librarySort, sortRuns, type LibrarySort, type SortedRun } from '../sort'
import type { ReferenceVoice, RunInfo, SongInfo, VersionState } from '../types'
import { octaveOptions, stepOptions, strengthOptions } from '../voiceChoices'
import { focusedSong, focusRequest, openOnLogicPage, openStems, view } from '../view'
import PlaylistToggle from './PlaylistToggle.vue'

const props = defineProps<{
  runs: RunInfo[]
  loading: boolean
  error: string | null
  /** Songs the worker is working on; they cannot be rendered again meanwhile. */
  busyIds: Set<string>
  /** ChangeMyVoice and StemMyWav are configured, so songs can be sung with another voice. */
  voices: boolean
  /** StemMyWav is configured, so songs can be split into stems on the voices page. */
  stems?: boolean
  /** Versions as the event stream reports them, newer than the listing while they are in the works. */
  liveVersions: VersionState[]
}>()

const emit = defineEmits<{
  template: [run: RunInfo]
  /** The song's score for the next song, together with its run's style and lyrics. */
  /** `warnings` only when the score came from a MIDI file, which then replaces the song's own. */
  useScore: [run: RunInfo, song: SongInfo, abc: string, warnings?: string[]]
  /** Something was deleted; carries the run's title for the notice. */
  deleted: [title: string]
  notice: [message: string]
  error: [message: string]
}>()

const confirm = useConfirm()

/** What the search field holds; the list shows only the runs that match. */
const query = ref('')
const scope = ref<SearchScope>('titleStyle')
const scopes = computed(() => [
  { value: 'titleStyle', label: t('searchTitleStyle') },
  { value: 'lyrics', label: t('searchLyrics') },
])
const terms = computed(() => parseQuery(query.value))

/** Only songs with at least this many stars (5: only those with five); 0 shows every song. */
const minRating = ref(0)
const ratingFilters = computed(() => [
  { value: 0, label: t('ratingAll') },
  { value: 5, label: t('ratingOnly', { n: 5 }) },
  ...[4, 3, 2, 1].map((n) => ({ value: n, label: t('ratingAtLeast', { n }) })),
])

/** The songs of the run the rating filter lets through; the run itself (its size, deleting it) stays whole. */
function shownSongs(run: RunInfo): SongInfo[] {
  // Read here, so the list follows a rating that changes while filtered.
  const stars = ratings.value
  return minRating.value === 0 ? run.songs : run.songs.filter((song) => (stars[song.id] ?? 0) >= minRating.value)
}

/** The order of the list, beside search and rating filter. */
const sortOptions = computed(() =>
  (['newest', 'oldest', 'rating', 'longest', 'shortest'] as LibrarySort[]).map((value) => ({
    value,
    label: t(`sort_${value}`),
  })),
)

/** The runs the search and the rating filter let through, in the chosen order, each with the songs to list. */
const shownRuns = computed(() =>
  sortRuns(
    props.runs.filter((run) => matchesRun(run, terms.value, scope.value)),
    librarySort.value,
    ratings.value,
    shownSongs,
  ).filter((entry) => entry.songs.length > 0),
)
const searching = computed(() => terms.value.length > 0)
/** Some runs are hidden, by the search or the rating filter; the count of hits shows. */
const filtering = computed(() => searching.value || minRating.value > 0)

function clearSearch(): void {
  query.value = ''
  minRating.value = 0
}

const pageSize = 8
/** Index of the first run on the current page (DataView's `first`). */
const first = ref(0)
const list = ref<HTMLElement | null>(null)

// A new search starts on the first page; its hits would otherwise hide behind a page number.
watch([terms, scope, minRating], () => {
  first.value = 0
})

// A deletion can empty the last page; go back to the last one that still has runs.
watch(
  () => shownRuns.value.length,
  (length) => {
    if (first.value >= length && length > 0) {
      first.value = Math.floor((length - 1) / pageSize) * pageSize
    }
  },
)

/** The paginator sits below the list, so a new page starts at its top rather than wherever the old one ended. */
function pageChanged(value: number): void {
  first.value = value
  list.value?.scrollIntoView({ behavior: 'smooth', block: 'start' })
}

function anchor(songId: string): string {
  return `song-${songId}`
}

/**
 * A song's address (`#/songs/<run>/songN`) scrolls to it, once per request: a library reload must not pull the page
 * back. A song not listed yet (just finished, or the library still loading) is tried again with the next load.
 */
let scrolledFor = -1
watch(
  [focusRequest, () => props.runs, view],
  async () => {
    const id = focusedSong.value
    if (!id || view.value !== 'songs' || scrolledFor === focusRequest.value) {
      return
    }
    if (!props.runs.some((run) => run.songs.some((song) => song.id === id))) {
      return
    }
    // A link to a song the search hides (from the player or the playlist) must still show it.
    if (!shownRuns.value.some((entry) => entry.songs.some((song) => song.id === id))) {
      query.value = ''
      minRating.value = 0
      await nextTick()
    }
    const index = shownRuns.value.findIndex((entry) => entry.songs.some((song) => song.id === id))
    if (index < 0) {
      return
    }
    scrolledFor = focusRequest.value
    first.value = Math.floor(index / pageSize) * pageSize
    await nextTick()
    document.getElementById(anchor(id))?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  },
  { immediate: true },
)

/**
 * The player goes on with the songs below this one, as the library lists them, so a search, the rating filter or the
 * order is also a play queue ("everything with four stars", "the best first").
 */
function playSong(run: RunInfo, song: SongInfo): void {
  play(trackOf(run, song), libraryTracks(shownRuns.value.map((shown) => ({ ...shown.run, songs: shown.songs }))))
}

async function rateSong(song: SongInfo, rating: number | null | undefined): Promise<void> {
  try {
    await rate(song.id, rating ?? null)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

function isPlaying(song: SongInfo): boolean {
  return current.value?.id === song.id && playing.value
}

async function renderFull(song: SongInfo): Promise<void> {
  try {
    await render(song.id, 'full')
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

/** Scores opened with "View ABC" or used, by song id; a song's score does not change once it is written. */
const scores = ref<Record<string, string>>({})

async function score(song: SongInfo): Promise<string> {
  scores.value[song.id] ??= await songScore(song.id)
  return scores.value[song.id]!
}

async function useScore(run: RunInfo, song: SongInfo): Promise<void> {
  try {
    emit('useScore', run, song, await score(song))
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

/** Songs whose MIDI file is being read back into a score. */
const importing = ref(new Set<string>())

/** The song edited in Logic: its MIDI file as the score, with the song's style, lyrics and seed. */
function useMidi(run: RunInfo, song: SongInfo): void {
  pickMidiFile(async (file) => {
    importing.value.add(song.id)
    try {
      const midi = await midiToAbc(file)
      emit('useScore', run, song, midi.abc, midi.warnings)
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    } finally {
      importing.value.delete(song.id)
    }
  })
}

/** A song action beyond play, render, FLAC and ABC: an icon on a wide screen, a line of the "…" menu on a phone. */
interface SongAction {
  key: string
  label: string
  icon: string
  /** A download: a link rather than a command. */
  url?: string
  command?: () => void
  danger?: boolean
  disabled?: boolean
  loading?: boolean
}

function songActions(run: RunInfo, song: SongInfo): SongAction[] {
  const actions: SongAction[] = []
  if (song.hasAudio) {
    actions.push({ key: 'share', label: t('share'), icon: 'pi pi-share-alt', command: () => shareSong(song.id) })
    actions.push({
      key: 'export',
      label: t('exportSong'),
      icon: 'pi pi-download',
      command: () => openExport({ songId: song.id, title: run.title, style: run.style }),
    })
  }
  if (song.hasScore) {
    actions.push({
      key: 'score',
      label: t('useScore'),
      icon: 'pi pi-file-import',
      command: () => void useScore(run, song),
    })
  }
  const importingMidi = importing.value.has(song.id)
  actions.push({
    key: 'midi',
    label: t('useMidi'),
    icon: importingMidi ? 'pi pi-spin pi-spinner' : 'pi pi-file-arrow-up',
    command: () => useMidi(run, song),
    disabled: importingMidi,
    loading: importingMidi,
  })
  if (song.hasScore) {
    actions.push({
      key: 'logic',
      label: t('openInLogic'),
      icon: 'pi pi-file-export',
      command: () => openOnLogicPage(song.id),
    })
  }
  if (song.hasAudio || song.hasScore) {
    actions.push({ key: 'zip', label: t('zipTitle'), icon: 'pi pi-box', url: songZipUrl(song.id) })
  }
  if (props.voices && song.hasAudio) {
    actions.push({
      key: 'voice',
      label: t('singWithVoice'),
      icon: 'pi pi-user-edit',
      command: () => void startSinging(run, song),
    })
  }
  if (props.stems && song.hasAudio) {
    actions.push({ key: 'stems', label: t('splitStems'), icon: 'pi pi-sliders-v', command: () => openStems(song.id) })
  }
  const busy = props.busyIds.has(song.id)
  actions.push({
    key: 'delete',
    label: busy ? t('deleteBusy') : t('deleteSong'),
    icon: 'pi pi-trash',
    command: () => askDeleteSong(run, song),
    danger: true,
    disabled: busy,
  })
  return actions
}

/** One popup menu for every song; it takes the actions of the song whose "…" was tapped. */
const actionMenu = useTemplateRef<InstanceType<typeof Menu>>('actionMenu')
const menuActions = ref<SongAction[]>([])
const menuItems = computed(() =>
  menuActions.value.map((action) => ({
    label: action.label,
    icon: action.icon,
    url: action.url,
    command: action.command,
    disabled: action.disabled,
    danger: action.danger,
  })),
)

function openActions(event: Event, run: RunInfo, song: SongInfo): void {
  menuActions.value = songActions(run, song)
  actionMenu.value?.toggle(event)
}

async function toggleScore(song: SongInfo, event: Event): Promise<void> {
  if ((event.target as HTMLDetailsElement).open && !scores.value[song.id]) {
    try {
      await score(song)
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  }
}

/** Also a song of the run the worker has not written a folder for yet. */
function runBusy(run: RunInfo): boolean {
  return [...props.busyIds].some((id) => id.startsWith(`${run.id}/`))
}

/** Deleting cannot be undone (the files do not go to the Trash, that would not free the space), so it asks first. */
function askDelete(message: string, title: string, remove: () => Promise<void>): void {
  confirm.require({
    header: t('confirmDelete'),
    message,
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: async () => {
      try {
        await remove()
        emit('deleted', title)
      } catch (caught) {
        emit('error', caught instanceof Error ? caught.message : String(caught))
      }
    },
  })
}

function askDeleteRun(run: RunInfo): void {
  const title = run.title || t('untitled')
  askDelete(t('confirmDeleteRun', { count: run.songs.length, title, size: formatBytes(run.bytes) }), title, () =>
    deleteRun(run.id),
  )
}

function askDeleteSong(run: RunInfo, song: SongInfo): void {
  const title = run.title || t('untitled')
  const params = { song: t('songN', { n: song.index }), title, size: formatBytes(song.bytes) }
  askDelete(
    run.songs.length > 1 ? t('confirmDeleteSong', params) : t('confirmDeleteLastSong', params),
    `${title} – ${params.song}`,
    () => deleteSong(song.id),
  )
}

/** The run being renamed; the dialog shows while it is set. */
const renaming = ref<RunInfo | null>(null)
const newTitle = ref('')
const savingTitle = ref(false)

function startRename(run: RunInfo): void {
  renaming.value = run
  newTitle.value = run.title
}

async function saveTitle(): Promise<void> {
  const run = renaming.value
  if (!run) {
    return
  }
  savingTitle.value = true
  try {
    const title = newTitle.value.trim()
    // The library reloads on the server's event, in every open browser alike.
    await renameRun(run.id, title)
    renaming.value = null
    emit('notice', t('renamed', { title: title || run.originalTitle || t('untitled') }))
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    savingTitle.value = false
  }
}

// ---- Versions: the song sung with another voice ------------------------------------------------------

/** The song's versions, each in its latest state: the listing's, or the event stream's while one is in the works. */
function versionsOf(song: SongInfo): VersionState[] {
  const live = new Map(props.liveVersions.filter((v) => v.songId === song.id).map((v) => [v.id, v]))
  const listed = song.versions.map((v) => live.get(v.id) ?? v)
  const known = new Set(listed.map((v) => v.id))
  // Asked for since the library loaded; it reloads when a version is queued, but the event may come first.
  const added = [...live.values()].filter((v) => !known.has(v.id) && v.stage !== 'cancelled')
  return [...listed, ...added]
}

function playVersion(run: RunInfo, song: SongInfo, version: VersionState): void {
  play(versionTrack(run, song, version))
}

function isPlayingVersion(song: SongInfo, version: VersionState): boolean {
  return current.value?.id === `${song.id}@${version.id}` && playing.value
}

/** The song the voice dialog is for; it shows while set. */
const singing = ref<{ run: RunInfo; song: SongInfo } | null>(null)
const voiceList = ref<ReferenceVoice[]>([])
const voicesLoading = ref(false)
const voiceChoice = ref<string | null>(null)
const octave = ref(0)
const strength = ref(0.7)
const steps = ref(50)
const keepReverb = ref(true)
const queueing = ref(false)

const octaves = computed(octaveOptions)
const strengths = computed(strengthOptions)
const stepChoices = computed(stepOptions)

async function startSinging(run: RunInfo, song: SongInfo): Promise<void> {
  singing.value = { run, song }
  voicesLoading.value = true
  try {
    voiceList.value = await listVoices()
    if (!voiceList.value.some((v) => v.id === voiceChoice.value)) {
      voiceChoice.value = voiceList.value[0]?.id ?? null
    }
  } catch (caught) {
    singing.value = null
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    voicesLoading.value = false
  }
}

async function sing(): Promise<void> {
  const target = singing.value
  if (!target || !voiceChoice.value) {
    return
  }
  queueing.value = true
  try {
    const version = await addVersion(target.song.id, {
      voiceId: voiceChoice.value,
      semiToneShift: octave.value,
      strength: strength.value,
      diffusionSteps: steps.value,
      keepReverb: keepReverb.value,
    })
    singing.value = null
    emit('notice', t('versionQueued', { title: target.run.title || t('untitled'), voice: version.voiceLabel }))
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    queueing.value = false
  }
}

/** Stops one in the works right away; a finished one is audio of its own, so that asks first. */
function removeVersion(run: RunInfo, song: SongInfo, version: VersionState): void {
  const remove = async () => {
    try {
      await deleteVersion(song.id, version.id)
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  }
  if (!version.finished) {
    void remove()
    return
  }
  confirm.require({
    header: t('confirmDelete'),
    message: t('confirmDeleteVersion', {
      voice: version.voiceLabel,
      song: t('songN', { n: song.index }),
      title: run.title || t('untitled'),
    }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: remove,
  })
}

/**
 * What the version was made with, so versions of one song can be told apart later: the choices of the dialog in its
 * words where they match one, else the number, then when and with which separation model.
 */
function versionSettings(version: VersionState): string {
  const shift = version.semiToneShift
  const octave = octaves.value.find((o) => o.value === shift)
  const strength = strengths.value.find((s) => s.value === version.strength)?.label ?? String(version.strength)
  const quality =
    stepChoices.value.find((s) => s.value === version.diffusionSteps)?.label ?? String(version.diffusionSteps)
  return [
    octave?.label ?? t('semitones', { n: shift > 0 ? `+${shift}` : String(shift).replace('-', '−') }),
    t('settingStrength', { value: strength }),
    t('settingSteps', { value: quality }),
    version.keepReverb ? t('withReverb') : t('withoutReverb'),
    ...(version.stemModel ? [t('stemModel', { model: version.stemModel })] : []),
    formatDateTime(version.createdAt),
  ].join(' · ')
}

const severityByQuality: Record<string, string> = {
  draft: 'warning',
  full: 'success',
}
</script>

<template>
  <section ref="list">
    <p v-if="error" class="danger">{{ t('libraryError', { message: error }) }}</p>
    <p v-else-if="!loading && runs.length === 0" class="muted">{{ t('libraryEmpty') }}</p>
    <form v-if="runs.length > 0" class="search" role="search" @submit.prevent>
      <InputText
        v-model="query"
        type="search"
        :placeholder="scope === 'lyrics' ? t('searchLyricsPlaceholder') : t('searchPlaceholder')"
        :aria-label="t('search')"
        enterkeyhint="search"
        autocomplete="off"
        class="min-w-0 basis-full sm:flex-1 sm:basis-0"
      />
      <SelectButton
        v-model="scope"
        :options="scopes"
        option-label="label"
        option-value="value"
        :allow-empty="false"
        :aria-label="t('searchScope')"
        size="small"
      />
      <Select
        v-model="minRating"
        :options="ratingFilters"
        option-label="label"
        option-value="value"
        :aria-label="t('ratingFilter')"
        v-tooltip.top="t('ratingFilter')"
        size="small"
        class="shrink-0"
      />
      <Select
        v-model="librarySort"
        :options="sortOptions"
        option-label="label"
        option-value="value"
        :aria-label="t('sortBy')"
        v-tooltip.top="t('sortBy')"
        size="small"
        class="shrink-0"
      />
    </form>
    <p v-if="filtering" class="muted text-sm mt-2 mb-0">
      {{ shownRuns.length === 0 ? t('searchNone') : t('searchHits', { count: shownRuns.length, total: runs.length }) }}
      <Button :label="t('searchClear')" text size="small" @click="clearSearch" />
    </p>
    <DataView
      :value="shownRuns"
      data-key="id"
      paginator
      :rows="pageSize"
      :first="first"
      :page-link-size="3"
      :always-show-paginator="false"
      @update:first="pageChanged"
    >
      <!-- The paragraph above already says the library is empty; without this slot DataView adds its own text. -->
      <template #empty />
      <template #list="slotProps">
        <div v-for="{ run, songs } in slotProps.items as SortedRun[]" :key="run.id">
          <!-- A fieldset is as wide as its content by default, which pushed a card past a phone's edge. -->
          <Fieldset :legend="run.title || t('untitled')" class="min-w-0">
            <div class="flex justify-between items-center">
              <div>
                <span v-if="run.createdAt" class="muted text-sm">{{ formatDateTime(run.createdAt) }} · </span>
                <span class="muted text-sm">{{ formatBytes(run.bytes) }}</span>
              </div>
              <div class="flex">
                <Button
                  icon="pi pi-pencil"
                  v-tooltip="t('rename')"
                  :aria-label="t('rename')"
                  text
                  @click="startRename(run)"
                />
                <Button
                  icon="pi pi-upload"
                  v-tooltip="t('useAsTemplate')"
                  :aria-label="t('useAsTemplate')"
                  text
                  @click="emit('template', run)"
                />
                <Button
                  v-if="run.songs.length > 1"
                  icon="pi pi-trash"
                  v-tooltip="runBusy(run) ? t('deleteBusy') : t('deleteRun')"
                  :aria-label="t('deleteRun')"
                  text
                  severity="danger"
                  :disabled="runBusy(run)"
                  @click="askDeleteRun(run)"
                />
              </div>
            </div>
            <p class="style muted">{{ run.style }}</p>
            <!-- Where the lyrics matched, so a hit makes sense without opening the whole text. -->
            <ul v-if="searching && scope === 'lyrics'" class="hits">
              <li v-for="(line, index) in matchingLines(run.lyrics, terms)" :key="index">
                <template v-for="(segment, part) in line" :key="part">
                  <mark v-if="segment.hit" class="bg-highlight">{{ segment.text }}</mark>
                  <template v-else>{{ segment.text }}</template>
                </template>
              </li>
            </ul>
            <div class="flex top-0">
              <div class="extras flex-1">
                <details v-if="run.lyrics" class="lyrics">
                  <summary>{{ t('showLyrics') }}</summary>
                  <pre>{{ run.lyrics }}</pre>
                </details>
              </div>
              <Button
                as="a"
                text
                v-if="run.songs.length > 1"
                v-tooltip="t('zipRun')"
                icon="pi pi-box"
                :href="runZipUrl(run.id)"
              />
            </div>

            <ul class="songs">
              <li
                v-for="song in songs"
                :id="anchor(song.id)"
                :key="song.id"
                :class="['song', { 'focused bg-emphasis': focusedSong === song.id }]"
              >
                <div class="flex flex-wrap gap-x-3 gap-y-1 items-center">
                  <Button
                    v-if="song.hasAudio"
                    :icon="isPlaying(song) ? 'pi pi-pause' : 'pi pi-play'"
                    rounded
                    :outlined="current?.id !== song.id"
                    size="small"
                    :aria-label="isPlaying(song) ? t('pause') : t('play')"
                    @click="playSong(run, song)"
                  />
                  <div>
                    <strong>{{ t('songN', { n: song.index }) }}</strong>
                  </div>
                  <div v-if="song.seconds" class="muted">{{ formatDuration(song.seconds) }}</div>
                  <Tag v-if="song.quality" :severity="severityByQuality[song.quality]">
                    {{ song.quality === 'draft' ? t('qualityDraft') : t('qualityFull') }}
                  </Tag>
                  <div v-if="song.seed !== null" class="muted seed">#{{ song.seed }}</div>
                  <Rating
                    :model-value="ratingOf(song.id)"
                    :aria-label="t('rating')"
                    class="ml-auto"
                    @update:model-value="rateSong(song, $event)"
                  />
                </div>
                <div class="flex justify-between">
                  <Button
                    v-if="song.canRender && song.quality !== 'full'"
                    :label="busyIds.has(song.id) ? t('rendering') : t('renderFull')"
                    :loading="busyIds.has(song.id) ? true : false"
                    text
                    size="small"
                    :disabled="busyIds.has(song.id)"
                    class="whitespace-nowrap"
                    @click="renderFull(song)"
                  />
                  <div class="ml-auto flex items-center justify-end">
                    <Button as="a" text v-if="song.hasAudio" size="small" :href="audioUrl(song.id, true)">{{
                      t('download')
                    }}</Button>
                    <Button as="a" text v-if="song.hasScore" size="small" :href="scoreUrl(song.id)">{{
                      t('score')
                    }}</Button>
                    <PlaylistToggle
                      v-if="song.hasAudio"
                      :song-id="song.id"
                      size="small"
                      @error="emit('error', $event)"
                    />
                    <!-- On a phone the other actions would push the card past the screen's edge, so they go into a menu. -->
                    <div class="hidden sm:flex">
                      <Button
                        v-for="action in songActions(run, song)"
                        :key="action.key"
                        :as="action.url ? 'a' : undefined"
                        :href="action.url"
                        :icon="action.icon"
                        text
                        size="small"
                        rounded
                        :severity="action.danger ? 'danger' : undefined"
                        v-tooltip="action.label"
                        :aria-label="action.label"
                        :loading="action.loading"
                        :disabled="action.disabled"
                        @click="action.command?.()"
                      />
                    </div>
                    <Button
                      icon="pi pi-ellipsis-v"
                      text
                      size="small"
                      rounded
                      severity="secondary"
                      class="sm:hidden"
                      :aria-label="t('songActions')"
                      aria-haspopup="true"
                      @click="openActions($event, run, song)"
                    />
                  </div>
                </div>
                <ul v-if="versionsOf(song).length > 0" class="versions">
                  <li v-for="version in versionsOf(song)" :key="version.id" class="flex flex-wrap items-center gap-x-2">
                    <Button
                      v-if="version.stage === 'done'"
                      :icon="isPlayingVersion(song, version) ? 'pi pi-pause' : 'pi pi-play'"
                      rounded
                      text
                      size="small"
                      :aria-label="isPlayingVersion(song, version) ? t('pause') : t('play')"
                      @click="playVersion(run, song, version)"
                    />
                    <span class="pi pi-user muted px-2" v-else aria-hidden="true" />
                    <span>{{ version.voiceLabel }}</span>
                    <Tag v-if="version.stage !== 'done'" :severity="version.stage === 'failed' ? 'danger' : undefined">
                      {{ t(`versionStage_${version.stage}`) }}
                      {{ versionProgress(version) }}
                    </Tag>
                    <div class="ml-auto flex">
                      <Button
                        v-if="version.stage === 'done'"
                        as="a"
                        text
                        size="small"
                        :href="versionAudioUrl(song.id, version.id, true)"
                        >{{ t('download') }}</Button
                      >
                      <Button
                        :icon="version.finished ? 'pi pi-trash' : 'pi pi-times'"
                        text
                        rounded
                        size="small"
                        :severity="version.finished ? 'danger' : 'secondary'"
                        v-tooltip="version.finished ? t('deleteVersion') : t('cancel')"
                        :aria-label="version.finished ? t('deleteVersion') : t('cancel')"
                        @click="removeVersion(run, song, version)"
                      />
                    </div>
                    <span class="muted basis-full pb-1 text-xs">{{ versionSettings(version) }}</span>
                    <span v-if="version.stage === 'failed' && version.message" class="danger basis-full text-sm">
                      {{ version.message }}
                    </span>
                  </li>
                </ul>
                <!-- One player for the whole page (PlayerBar.vue), so the song keeps playing on the other pages. -->
                <span v-if="!song.hasAudio" class="muted">{{ t('noAudio') }}</span>
                <details v-if="song.hasScore" class="score" @toggle="toggleScore(song, $event)">
                  <summary>{{ t('showScore') }}</summary>
                  <pre>{{ scores[song.id] ?? '…' }}</pre>
                </details>
              </li>
            </ul>
          </Fieldset>
        </div>
      </template>
    </DataView>
    <Menu ref="actionMenu" :model="menuItems" popup>
      <!-- Only so "delete" is red like its icon on a wide screen. -->
      <template #item="{ item, props: link }">
        <a v-bind="link.action" :href="item.url" :class="{ danger: item.danger }">
          <span :class="[item.icon, 'p-menu-item-icon', { danger: item.danger }]" />
          <span class="p-menu-item-label">{{ item.label }}</span>
        </a>
      </template>
    </Menu>
    <Dialog
      :visible="renaming !== null"
      modal
      :header="t('rename')"
      :draggable="false"
      :style="{ width: 'min(28rem, calc(100vw - 2rem))' }"
      @update:visible="(open: boolean) => !open && (renaming = null)"
    >
      <form v-if="renaming" class="flex flex-col gap-3" @submit.prevent="saveTitle">
        <InputText
          v-model="newTitle"
          :placeholder="renaming.originalTitle"
          :maxlength="200"
          autofocus
          fluid
          aria-describedby="rename-hint"
        />
        <p id="rename-hint" class="muted text-sm m-0">
          {{ t('renameHint', { title: renaming.originalTitle || t('untitled') }) }}
        </p>
        <div class="flex justify-end gap-2">
          <Button type="button" :label="t('cancel')" severity="secondary" text @click="renaming = null" />
          <Button type="submit" :label="t('save')" :loading="savingTitle" />
        </div>
      </form>
    </Dialog>
    <Dialog
      :visible="singing !== null"
      modal
      :header="t('singWithVoice')"
      :draggable="false"
      :style="{ width: 'min(30rem, calc(100vw - 2rem))' }"
      @update:visible="(open: boolean) => !open && (singing = null)"
    >
      <form v-if="singing" class="flex flex-col gap-4" @submit.prevent="sing">
        <p class="muted m-0 text-sm">
          {{
            t('singIntro', { title: singing.run.title || t('untitled'), song: t('songN', { n: singing.song.index }) })
          }}
        </p>
        <p v-if="!voicesLoading && voiceList.length === 0" class="m-0">{{ t('singNoVoices') }}</p>
        <div v-else class="flex flex-col gap-1">
          <label for="sing-voice" class="muted text-sm">{{ t('voice') }}</label>
          <Select
            v-model="voiceChoice"
            input-id="sing-voice"
            :options="voiceList"
            option-label="label"
            option-value="id"
            :loading="voicesLoading"
            fluid
          />
        </div>
        <div class="flex flex-col gap-1">
          <span class="muted text-sm">{{ t('octave') }}</span>
          <SelectButton
            v-model="octave"
            :options="octaves"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-label="t('octave')"
          />
        </div>
        <div class="flex flex-col gap-1">
          <span class="muted text-sm">{{ t('strength') }}</span>
          <SelectButton
            v-model="strength"
            :options="strengths"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-label="t('strength')"
          />
        </div>
        <div class="flex flex-col gap-1">
          <span class="muted text-sm">{{ t('steps') }}</span>
          <SelectButton
            v-model="steps"
            :options="stepChoices"
            option-label="label"
            option-value="value"
            :allow-empty="false"
            :aria-label="t('steps')"
          />
        </div>
        <div class="flex items-center gap-2">
          <Checkbox v-model="keepReverb" input-id="sing-reverb" binary />
          <label for="sing-reverb">{{ t('keepReverb') }}</label>
        </div>
        <p class="muted m-0 text-sm">{{ t('singHint') }}</p>
        <div class="flex justify-end gap-2">
          <Button type="button" :label="t('cancel')" severity="secondary" text @click="singing = null" />
          <Button type="submit" :label="t('sing')" :loading="queueing" :disabled="!voiceChoice || voicesLoading" />
        </div>
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
/* On a phone the field takes the first line, scope, rating filter and order share the second. */
.search {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 0.5rem;
  margin-bottom: 0.5rem;
}

.hits {
  margin: 0.4rem 0 0;
  padding: 0 0 0 0.75rem;
  border-left: 2px solid var(--p-primary-color);
  font-size: 0.9rem;
  font-style: italic;
  list-style: none;
}

.hits mark {
  border-radius: 2px;
  font-style: normal;
}

.style {
  display: -webkit-box;
  margin: 0.5rem 0 0;
  overflow: hidden;
  font-size: 0.9rem;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  line-clamp: 2;
}

.extras {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-start;
  gap: 0.4rem 1rem;
  margin-top: 0.4rem;
}

.lyrics {
  flex: 1 1 100%;
  font-size: 0.9rem;
}

/* Beside the closed "Lyrics" summary; an open one takes the whole row and the link moves below it. */
.lyrics:not([open]) {
  flex: 0 1 auto;
}

.score {
  font-size: 0.9rem;
}

.lyrics summary,
.score summary {
  cursor: pointer;
  color: var(--accent);
}

.lyrics pre,
.score pre {
  max-height: 20rem;
  margin: 0.5rem 0 0;
  padding: 0.5rem 0.75rem;
  overflow: auto;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
  font-family: var(--font-mono);
  font-size: 0.8rem;
  white-space: pre-wrap;
}

.songs {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
  margin: 0.9rem 0 0;
  padding: 0.9rem 0 0;
  border-top: 1px solid var(--border);
  list-style: none;
}

.song {
  display: flex;
  flex-direction: column;
  gap: 0.4rem;
}

.focused {
  margin: -0.5rem;
  padding: 0.5rem;
  border-radius: var(--radius-small);
}

.versions {
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
  margin: 0;
  padding: 0 0 0 0.75rem;
  border-left: 2px solid var(--border);
  font-size: 0.9rem;
  list-style: none;
}

.seed {
  font-family: var(--font-mono);
  font-size: 0.8rem;
}
</style>
