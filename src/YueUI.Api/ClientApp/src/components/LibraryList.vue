<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import {
  audioUrl,
  deleteRun,
  deleteSong,
  downloadLogicProject,
  renameRun,
  render,
  runZipUrl,
  scoreUrl,
  songScore,
  songZipUrl,
} from '../api'
import { formatBytes, formatDateTime, formatDuration, t } from '../i18n'
import { current, libraryTracks, play, playing, trackOf } from '../player'
import { playlistIds, toggleInPlaylist } from '../playlist'
import type { RunInfo, SongInfo } from '../types'
import { focusedSong, focusRequest, view } from '../view'

const props = defineProps<{
  runs: RunInfo[]
  loading: boolean
  error: string | null
  /** Songs the worker is working on; they cannot be rendered again meanwhile. */
  busyIds: Set<string>
  /** A yue-to-logic-pro server is configured, so songs can be opened in Logic Pro. */
  logicExport: boolean
}>()

const emit = defineEmits<{
  template: [run: RunInfo]
  /** The song's score for the next song, together with its run's style and lyrics. */
  useScore: [run: RunInfo, song: SongInfo, abc: string]
  /** Something was deleted; carries the run's title for the notice. */
  deleted: [title: string]
  notice: [message: string]
  error: [message: string]
}>()

const confirm = useConfirm()

const pageSize = 8
const shown = ref(pageSize)
const visible = computed(() => props.runs.slice(0, shown.value))

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
    const index = props.runs.findIndex((run) => run.songs.some((song) => song.id === id))
    if (index < 0) {
      return
    }
    scrolledFor = focusRequest.value
    if (index >= shown.value) {
      shown.value = Math.ceil((index + 1) / pageSize) * pageSize
    }
    await nextTick()
    document.getElementById(anchor(id))?.scrollIntoView({ behavior: 'smooth', block: 'center' })
  },
  { immediate: true },
)

/** The player goes on with the songs below this one, as the library lists them. */
function playSong(run: RunInfo, song: SongInfo): void {
  play(trackOf(run, song), libraryTracks(props.runs))
}

function isPlaying(song: SongInfo): boolean {
  return current.value?.id === song.id && playing.value
}

async function togglePlaylist(song: SongInfo): Promise<void> {
  try {
    await toggleInPlaylist(song.id)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

async function renderFull(song: SongInfo): Promise<void> {
  try {
    await render(song.id, 'full')
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

/** Songs whose Logic project is being built; uploading a long FLAC and building take a while. */
const exporting = ref(new Set<string>())

async function openInLogic(song: SongInfo): Promise<void> {
  exporting.value.add(song.id)
  try {
    const warnings = await downloadLogicProject(song.id)
    if (warnings.length > 0) {
      emit('notice', t('logicWarnings', { messages: warnings.map((w) => w.message).join(' ') }))
    }
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  } finally {
    exporting.value.delete(song.id)
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

const severityByQuality: Record<string, string> = {
  draft: 'warning',
  full: 'success',
}
</script>

<template>
  <section>
    <p v-if="error" class="danger">{{ t('libraryError', { message: error }) }}</p>
    <p v-else-if="!loading && runs.length === 0" class="muted">{{ t('libraryEmpty') }}</p>
    <DataView :value="visible">
      <template #list="slotProps">
        <div v-for="(run, index) in slotProps.items" :key="index">
          <Fieldset :legend="run.title || t('untitled')">
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
                v-for="song in run.songs"
                :id="anchor(song.id)"
                :key="song.id"
                :class="['song', { 'focused bg-emphasis': focusedSong === song.id }]"
              >
                <div class="flex gap-3 items-center">
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
                </div>
                <div class="flex justify-between">
                  <Button
                    v-if="song.canRender && song.quality !== 'full'"
                    :label="busyIds.has(song.id) ? t('rendering') : t('renderFull')"
                    :loading="busyIds.has(song.id) ? true : false"
                    text
                    size="small"
                    :disabled="busyIds.has(song.id)"
                    @click="renderFull(song)"
                  />
                  <div class="flex justify-end">
                    <Button as="a" text v-if="song.hasAudio" size="small" :href="audioUrl(song.id, true)">{{
                      t('download')
                    }}</Button>
                    <Button as="a" text v-if="song.hasScore" size="small" :href="scoreUrl(song.id)">{{
                      t('score')
                    }}</Button>
                    <Button
                      v-if="song.hasAudio"
                      :icon="playlistIds.includes(song.id) ? 'pi pi-check-circle' : 'pi pi-plus-circle'"
                      text
                      size="small"
                      rounded
                      v-tooltip="playlistIds.includes(song.id) ? t('removeFromPlaylist') : t('addToPlaylist')"
                      :aria-label="playlistIds.includes(song.id) ? t('removeFromPlaylist') : t('addToPlaylist')"
                      @click="togglePlaylist(song)"
                    />
                    <Button
                      v-if="song.hasScore"
                      icon="pi pi-file-import"
                      text
                      size="small"
                      rounded
                      v-tooltip="t('useScore')"
                      :aria-label="t('useScore')"
                      @click="useScore(run, song)"
                    />
                    <Button
                      v-if="logicExport && song.hasAudio && song.hasScore"
                      icon="pi pi-file-export"
                      text
                      size="small"
                      rounded
                      v-tooltip="t('openInLogic')"
                      :aria-label="t('openInLogic')"
                      :loading="exporting.has(song.id)"
                      :disabled="exporting.has(song.id)"
                      @click="openInLogic(song)"
                    />
                    <Button
                      v-if="song.hasAudio || song.hasScore"
                      as="a"
                      text
                      size="small"
                      rounded
                      icon="pi pi-box"
                      v-tooltip="t('zipTitle')"
                      :href="songZipUrl(song.id)"
                      :title="t('zipTitle')"
                    />
                    <Button
                      icon="pi pi-trash"
                      text
                      size="small"
                      rounded
                      severity="danger"
                      v-tooltip="busyIds.has(song.id) ? t('deleteBusy') : t('deleteSong')"
                      :aria-label="t('deleteSong')"
                      :disabled="busyIds.has(song.id)"
                      @click="askDeleteSong(run, song)"
                    />
                  </div>
                </div>
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
      <template #footer>
        <Button v-if="runs.length > shown" @click="shown += pageSize">
          {{ t('showMore') }}
        </Button>
      </template>
    </DataView>
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
  </section>
</template>

<style scoped>
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

.zip-run {
  line-height: 1.5;
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

.seed {
  font-family: var(--font-mono);
  font-size: 0.8rem;
}

.links {
  display: flex;
  gap: 0.9rem;
  margin-left: auto;
}

.badge.full {
  background: var(--accent-soft);
  color: var(--accent);
}

.more {
  align-self: center;
}
</style>
