<script setup lang="ts">
import { computed, ref, useTemplateRef, watch } from 'vue'
import InputGroup from 'primevue/inputgroup'
import InputGroupAddon from 'primevue/inputgroupaddon'
import Message from 'primevue/message'
import {
  ApiError,
  assignInstrument,
  convertScore,
  exportLogicProject,
  listAssignments,
  listInstruments,
  LogicExportError,
  type ScoreSource,
} from '../logic/api'
import { t } from '../logic/i18n'
import { instrumentsForExport, withDrumNotes, withInstrumentChannels } from '../logic/instruments'
import { deletePreset, loadPresets, savePreset, type Preset } from '../logic/presets'
import { clearFormState, defaultFormState, loadFormState, saveFormState, toConversionOptions } from '../logic/options'
import { baseName, download } from '../logic/score'
import type { Assignments, ConversionResult, Diagnostic, Instrument } from '../logic/types'
import type { RunInfo, SongInfo } from '../types'
import { logicSong, replaceLogicSong } from '../view'
import FileDropZone from './logic/FileDropZone.vue'
import InstrumentDialog from './logic/InstrumentDialog.vue'
import OptionsForm from './logic/OptionsForm.vue'
import ResultView from './logic/ResultView.vue'
import ScorePreview from './logic/ScorePreview.vue'

/**
 * yue-to-logic-pro's page inside YuE UI: a score becomes a MIDI file and, with its audio, a Logic Pro project, with
 * every arrangement option, the instrument library and a MIDI preview. The score is a song of the library, which the
 * server reads itself, or a score.abc brought along, as on the original page.
 */
const props = defineProps<{ runs: RunInfo[] }>()

type SourceMode = 'library' | 'upload'
/** A song of the library is the usual source here; a score.abc from elsewhere is the exception. */
const mode = ref<SourceMode>('library')
const modes = computed(() => [
  { label: t('sourceLibrary'), value: 'library' },
  { label: t('sourceUpload'), value: 'upload' },
])

// ---- Library song -----------------------------------------------------------------------------------

/** Every song that has a score, labelled "<title> · songN"; one without a score has nothing to convert. */
const songOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((song) => song.hasScore)
      .map((song) => ({ label: `${run.title || t('untitled')} · ${t('songN', { n: song.index })}`, value: song.id })),
  ),
)
const librarySongs = computed(
  () => new Map(props.runs.flatMap((run) => run.songs.map((song) => [song.id, { run, song }] as const))),
)
const songId = ref<string | null>(logicSong.value)
const chosen = computed<{ run: RunInfo; song: SongInfo } | null>(() =>
  songId.value ? (librarySongs.value.get(songId.value) ?? null) : null,
)
/** A song the address names that the library does not list (deleted, or the library is not loaded yet). */
const songMissing = computed(() => songId.value !== null && props.runs.length > 0 && !chosen.value)

/** A file name out of a title: the characters a file system refuses would otherwise reach the download. */
function fileNameOf(title: string): string {
  return title.replace(/[\\/:*?"<>|]+/g, '-').trim() || 'score'
}

function pickSong(id: string | null): void {
  songId.value = id
  replaceLogicSong(id)
  forgetResult()
  const found = id ? librarySongs.value.get(id) : undefined
  if (found) {
    outputName.value = fileNameOf(found.run.title)
  }
}

/** A song opened from the library is converted as soon as the page can, so its preview is there to adjust. */
let convertWhenReady = songId.value !== null

// An address with a song (a link from the library) picks it, also while the page is already open.
watch(logicSong, (id) => {
  if (id && id !== songId.value) {
    mode.value = 'library'
    pickSong(id)
    convertWhenReady = true
  }
})

// The library arrives after the page: a song picked by address gets its name once its title is known.
watch(chosen, (found, before) => {
  if (found && !before && outputName.value === 'score') {
    outputName.value = fileNameOf(found.run.title)
  }
})

// ---- Uploaded files ---------------------------------------------------------------------------------

const file = ref<File | null>(null)
const audio = ref<File | null>(null)

function selectFile(selected: File): void {
  file.value = selected
  outputName.value = baseName(selected.name)
  forgetResult()
}

function selectAudio(selected: File | null): void {
  audio.value = selected
  logicError.value = null
  logicWarnings.value = []
}

/** What the server is asked to convert, or null while the chosen way has nothing to offer. */
const source = computed<ScoreSource | null>(() => {
  if (mode.value === 'library') {
    return chosen.value ? { song: chosen.value.song.id } : null
  }
  return file.value ? { file: file.value, audio: audio.value } : null
})
/** A library song brings its own audio.flac, which the server takes from the song's folder. */
const hasAudio = computed(() => (mode.value === 'library' ? !!chosen.value?.song.hasAudio : audio.value !== null))

watch(mode, () => forgetResult())

// Waits for the library as well, which may arrive after the page when it is opened by address.
watch(
  source,
  (from) => {
    if (convertWhenReady && from && mode.value === 'library') {
      convertWhenReady = false
      void convert()
    }
  },
  { flush: 'post' },
)

function forgetResult(): void {
  pending?.abort()
  pending = null
  busy.value = false
  result.value = null
  stale.value = false
  error.value = null
  logicError.value = null
  logicWarnings.value = []
}

// ---- Instruments ------------------------------------------------------------------------------------

/** The instrument library and which track plays which, both kept on the server. */
const instruments = ref<Instrument[]>([])
/**
 * Which track plays which instrument. This is configuration, not playback: it decides the channel a track is
 * written on and the hardware the Logic project addresses, both of which the server produces. It therefore
 * applies in every browser - only sending the preview to a MIDI port needs Web MIDI, which Chromium alone has.
 */
const assignments = ref<Assignments>({})
const instrumentsError = ref<string | null>(null)
const instrumentDialog = useTemplateRef<InstanceType<typeof InstrumentDialog>>('instrumentDialog')
void loadInstruments()

async function loadInstruments(): Promise<void> {
  try {
    ;[instruments.value, assignments.value] = await Promise.all([listInstruments(), listAssignments()])
  } catch (caught) {
    // Without the library everything else still works; the routing table then shows ports and channels only.
    instrumentsError.value = t('instrumentsError', {
      message: caught instanceof Error ? caught.message : String(caught),
    })
  }
}

function openInstruments(): void {
  instrumentDialog.value?.open()
}

/** The list after the dialog changed it; a track whose instrument is gone loses its assignment, as on the server. */
function instrumentsChanged(list: Instrument[]): void {
  instruments.value = list
  const ids = new Set(list.map((instrument) => instrument.id))
  assignments.value = Object.fromEntries(Object.entries(assignments.value).filter(([, id]) => ids.has(id)))
}

/** Shown at once and sent to the server; if that fails the previous choice comes back. */
async function assign(track: string, instrumentId: number | null): Promise<void> {
  const before = { ...assignments.value }
  const next = { ...assignments.value }
  if (instrumentId === null) {
    delete next[track]
  } else {
    next[track] = instrumentId
  }
  assignments.value = next
  instrumentsError.value = null
  try {
    await assignInstrument(track, instrumentId)
  } catch (caught) {
    assignments.value = before
    instrumentsError.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : t('instrumentsError', { message: caught instanceof Error ? caught.message : String(caught) })
  }
}

/** The channel a track plays on depends on its instrument, so the downloads are only current for the assignment they were made with. */
watch(assignments, () => {
  if (result.value) {
    stale.value = true
  }
})

// ---- Presets ----------------------------------------------------------------------------------------

/** The presets, kept on the server like the instruments; loaded once, and again after every change. */
const presets = ref<Preset[]>([])
/** The preset the form currently shows; empty once a preset is saved under a new name or none is chosen. */
const presetName = ref('')
const newPresetName = ref('')
const presetsBusy = ref(false)
const presetsError = ref<string | null>(null)
void withPresets(() => loadPresets())

/** Runs a change of the presets and shows what came back; the form itself is unaffected by a failure. */
async function withPresets(change: () => Promise<Preset[]>): Promise<boolean> {
  presetsBusy.value = true
  presetsError.value = null
  try {
    presets.value = await change()
    return true
  } catch (caught) {
    presetsError.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : t('presetsError', { message: caught instanceof Error ? caught.message : String(caught) })
    return false
  } finally {
    presetsBusy.value = false
  }
}

function applyPreset(name: string | null): void {
  // The select's clear button hands over null; no preset chosen is an empty name, as before.
  presetName.value = name ?? ''
  const preset = presets.value.find((entry) => entry.name === presetName.value)
  if (preset) {
    form.value = { ...preset.form }
  }
}

async function storePreset(): Promise<void> {
  const name = newPresetName.value
  if (await withPresets(() => savePreset(name, form.value))) {
    // The server keeps the name as saved; pick it as it now stands so the select shows it.
    presetName.value = presets.value.find((entry) => entry.name.toLowerCase() === name.toLowerCase())?.name ?? ''
    newPresetName.value = ''
  }
}

async function removePreset(): Promise<void> {
  const name = presetName.value
  if (await withPresets(() => deletePreset(name))) {
    presetName.value = ''
  }
}

// ---- Conversion and export --------------------------------------------------------------------------

const logicBusy = ref(false)
const logicError = ref<string | null>(null)
const logicWarnings = ref<Diagnostic[]>([])
const form = ref(loadFormState())
const outputName = ref(chosen.value ? fileNameOf(chosen.value.run.title) : 'score')
const result = ref<ConversionResult | null>(null)
const stale = ref(false)
const busy = ref(false)
const error = ref<string | null>(null)

let pending: AbortController | null = null

watch(
  form,
  (value) => {
    saveFormState(value)
    if (result.value) {
      stale.value = true
    }
  },
  { deep: true },
)

/** The form's options with every assigned track on its instrument's channel, and the drums on a drum machine's notes. */
function conversionOptions() {
  const options = withInstrumentChannels(toConversionOptions(form.value), assignments.value, instruments.value)
  return withDrumNotes(options, assignments.value, instruments.value)
}

async function convert(): Promise<void> {
  const from = source.value
  if (!from) {
    return
  }

  pending?.abort()
  const controller = new AbortController()
  pending = controller
  busy.value = true
  error.value = null

  try {
    result.value = await convertScore(from, conversionOptions(), controller.signal)
    stale.value = false
  } catch (caught) {
    if (caught instanceof DOMException && caught.name === 'AbortError') {
      return
    }
    result.value = null
    error.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : t('requestError', { message: caught instanceof Error ? caught.message : String(caught) })
  } finally {
    if (pending === controller) {
      busy.value = false
      pending = null
    }
  }
}

async function exportLogic(): Promise<void> {
  const from = source.value
  if (!from) {
    return
  }

  logicBusy.value = true
  logicError.value = null
  logicWarnings.value = []
  try {
    const exported = await exportLogicProject(
      from,
      conversionOptions(),
      outputName.value,
      form.value.splitSections,
      instrumentsForExport(assignments.value, instruments.value),
    )
    logicWarnings.value = exported.warnings
    download(exported.zip, exported.fileName)
  } catch (caught) {
    logicError.value =
      caught instanceof LogicExportError
        ? `${t('logicFailed')}: ${caught.message}`
        : caught instanceof ApiError && caught.status === 0
          ? t('networkError')
          : `${t('logicFailed')}: ${caught instanceof Error ? caught.message : String(caught)}`
  } finally {
    logicBusy.value = false
  }
}

/** Back to a fresh start: no song or files, default parameters, no result. */
function reset(): void {
  forgetResult()
  file.value = null
  audio.value = null
  songId.value = null
  replaceLogicSong(null)
  form.value = defaultFormState()
  presetName.value = ''
  newPresetName.value = ''
  clearFormState()
  outputName.value = 'score'
  logicBusy.value = false
}
</script>

<template>
  <!-- minmax(0, 1fr): a wide table inside a card scrolls on its own instead of widening the page. -->
  <div class="grid grid-cols-[minmax(0,1fr)] gap-4">
    <Message v-if="instrumentsError" severity="error" role="alert">{{ instrumentsError }}</Message>

    <Card>
      <template #title>
        <div class="flex items-center justify-between gap-2">
          <h2 class="m-0">{{ t('pageTitle') }}</h2>
          <div class="flex gap-1">
            <Button
              icon="pi pi-sliders-h"
              text
              rounded
              :aria-label="t('instrumentsManage')"
              v-tooltip.bottom="t('instrumentsManageTitle')"
              @click="openInstruments"
            />
            <Button
              icon="pi pi-undo"
              text
              rounded
              :aria-label="t('reset')"
              v-tooltip.bottom="t('resetTitle')"
              @click="reset"
            />
          </div>
        </div>
      </template>
      <template #content>
        <SelectButton
          v-model="mode"
          :options="modes"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          :aria-label="t('source')"
          class="mb-4"
        />

        <div v-show="mode === 'library'" class="flex flex-col gap-2">
          <label for="logic-song" class="text-sm text-muted-color">{{ t('librarySong') }}</label>
          <Select
            :model-value="songId"
            input-id="logic-song"
            :options="songOptions"
            option-label="label"
            option-value="value"
            :placeholder="t('librarySongPick')"
            :empty-message="t('librarySongNone')"
            filter
            :filter-placeholder="t('librarySongFilter')"
            fluid
            @update:model-value="pickSong"
          />
          <p v-if="songMissing" class="hint warning">{{ t('librarySongMissing') }}</p>
          <p v-else-if="chosen && !chosen.song.hasAudio" class="hint muted">{{ t('librarySongNoAudio') }}</p>
          <p v-else class="hint muted">{{ t('librarySongHint') }}</p>
        </div>

        <!-- Score and audio side by side; on a phone each one takes the whole row. -->
        <div v-show="mode === 'upload'" class="grid gap-4 grid-cols-1 md:grid-cols-2 items-stretch">
          <div class="flex flex-col gap-2">
            <h3 class="m-0 text-base">{{ t('scoreTitle') }}</h3>
            <FileDropZone
              :file="file"
              extension=".abc"
              accept=".abc,text/plain,text/vnd.abc"
              :drop-hint="t('dropHint')"
              :wrong-type-hint="t('notAbc')"
              @select="selectFile"
              @clear="file = null"
            />
          </div>
          <div class="flex flex-col gap-2">
            <h3 class="m-0 text-base">{{ t('audioTitle') }}</h3>
            <FileDropZone
              :file="audio"
              extension=".flac"
              accept=".flac,audio/flac,audio/x-flac"
              :drop-hint="t('audioDropHint')"
              :wrong-type-hint="t('notFlac')"
              @select="selectAudio"
              @clear="selectAudio(null)"
            />
            <p class="hint muted mt-0">{{ t('audioInfo') }}</p>
          </div>
        </div>
      </template>
    </Card>

    <Card>
      <template #title>
        <div class="flex flex-wrap items-center justify-between gap-2">
          <h2 class="m-0">{{ t('optionsTitle') }}</h2>
          <div class="flex flex-wrap items-center gap-2 text-base font-normal">
            <Select
              :model-value="presetName || null"
              :options="presets"
              option-label="name"
              option-value="name"
              :placeholder="t('presetNone')"
              :aria-label="t('presets')"
              show-clear
              size="small"
              class="w-40"
              @update:model-value="applyPreset"
            />
            <InputText
              v-model.trim="newPresetName"
              :placeholder="t('presetName')"
              :aria-label="t('presetName')"
              spellcheck="false"
              size="small"
              class="w-32"
            />
            <Button
              :label="t('presetSave')"
              severity="secondary"
              outlined
              size="small"
              :disabled="!newPresetName || presetsBusy"
              @click="storePreset"
            />
            <Button
              :label="t('presetDelete')"
              severity="secondary"
              outlined
              size="small"
              :disabled="!presetName || presetsBusy"
              @click="removePreset"
            />
          </div>
        </div>
      </template>
      <template #content>
        <p v-if="presetsError" class="hint danger mt-0 mb-3" role="alert">{{ presetsError }}</p>
        <OptionsForm v-model="form" />

        <form class="submit" @submit.prevent="convert">
          <label class="grid gap-1 flex-[1_1_14rem] text-sm text-muted-color">
            {{ t('outputName') }}
            <InputGroup>
              <InputText v-model.trim="outputName" required spellcheck="false" />
              <InputGroupAddon>.mid</InputGroupAddon>
            </InputGroup>
          </label>
          <Button
            type="submit"
            :label="busy ? t('converting') : t('convert')"
            icon="pi pi-cog"
            :loading="busy"
            :disabled="!source || busy || !outputName"
          />
        </form>
        <p v-if="error" class="hint danger" role="alert">{{ error }}</p>
      </template>
    </Card>

    <ScorePreview
      v-if="result?.score"
      :score="result.score"
      :include-chords="form.includeChords"
      :stale="stale"
      :instruments="instruments"
      :assignments="assignments"
      @assign="assign"
      @manage-instruments="openInstruments"
    />

    <ResultView
      v-if="result"
      :result="result"
      :output-name="outputName"
      :stale="stale"
      :has-audio="hasAudio"
      :logic-busy="logicBusy"
      :logic-error="logicError"
      :logic-warnings="logicWarnings"
      @export-logic="exportLogic"
    />

    <InstrumentDialog ref="instrumentDialog" :instruments="instruments" @changed="instrumentsChanged" />
  </div>
</template>

<style scoped>
.submit {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  justify-content: space-between;
  gap: 1rem;
  margin-top: 1.5rem;
  padding-top: 1.25rem;
  border-top: 1px solid var(--border);
}
</style>
