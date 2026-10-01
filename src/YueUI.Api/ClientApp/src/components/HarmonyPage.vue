<script setup lang="ts">
import Checkbox from 'primevue/checkbox'
import Message from 'primevue/message'
import { computed, ref, watch } from 'vue'
import { ApiError, assignInstrument, convertScore, listAssignments, type ScoreSource } from '../logic/api'
import { t } from '../logic/i18n'
import { withInstrumentChannels } from '../logic/instruments'
import { instruments, reloadInstruments } from '../logic/instrumentLibrary'
import { defaultFormState, toConversionOptions } from '../logic/options'
import { baseName, download, midiBlob } from '../logic/score'
import { HARMONY_PARTS, type Assignments, type ConversionResult, type HarmonyPart } from '../logic/types'
import type { RunInfo } from '../types'
import { navigate } from '../view'
import FileDropZone from './logic/FileDropZone.vue'
import ScorePreview from './logic/ScorePreview.vue'

/**
 * Backing vocals as a tool of their own: a melody (a MIDI file or score.abc, or a song of the library) in, a MIDI file
 * with one track per part out, heard in the preview first. It runs the same conversion as the Logic page with only the
 * backing vocals switched on, so nothing here depends on the Logic template.
 */
const props = defineProps<{ runs: RunInfo[] }>()

type SourceMode = 'upload' | 'library'
/** A melody from elsewhere is what this tool is for; a library song is the other way in. */
const mode = ref<SourceMode>('upload')
const modes = computed(() => [
  { label: t('harmonySourceUpload'), value: 'upload' },
  { label: t('sourceLibrary'), value: 'library' },
])
const file = ref<File | null>(null)
const songId = ref<string | null>(null)
const songOptions = computed(() =>
  props.runs.flatMap((run) =>
    run.songs
      .filter((song) => song.hasScore)
      .map((song) => ({ label: `${run.title || t('untitled')} · ${t('songN', { n: song.index })}`, value: song.id })),
  ),
)

// ---- Settings, kept per browser like the Logic page's form --------------------------------------------------

interface HarmonySettings {
  parts: HarmonyPart[]
  key: string | null
  chorusOnly: boolean
  withChords: boolean
}

const storageKey = 'yue-ui.harmony'

function loadSettings(): HarmonySettings {
  const defaults: HarmonySettings = {
    parts: ['ThirdAbove', 'SixthBelow'],
    key: null,
    chorusOnly: false,
    withChords: false,
  }
  try {
    const saved = JSON.parse(localStorage.getItem(storageKey) ?? 'null') as Partial<HarmonySettings> | null
    return saved
      ? { ...defaults, ...saved, parts: (saved.parts ?? defaults.parts).filter((p) => HARMONY_PARTS.includes(p)) }
      : defaults
  } catch {
    return defaults
  }
}

const settings = ref<HarmonySettings>(loadSettings())
watch(
  settings,
  (value) => {
    try {
      localStorage.setItem(storageKey, JSON.stringify(value))
    } catch {
      // Remembering the choice is a convenience only.
    }
  },
  { deep: true },
)

const partLabels: Record<HarmonyPart, Parameters<typeof t>[0]> = {
  ThirdAbove: 'harmonyThirdAbove',
  ThirdBelow: 'harmonyThirdBelow',
  SixthBelow: 'harmonySixthBelow',
  Alto: 'harmonyAlto',
  Tenor: 'harmonyTenor',
  Bass: 'harmonyBass',
  Drone: 'harmonyDrone',
  DroneHeld: 'harmonyDroneHeld',
}
const majorKeys = ['C', 'Db', 'D', 'Eb', 'E', 'F', 'F#', 'G', 'Ab', 'A', 'Bb', 'B']
const minorKeys = ['Cm', 'C#m', 'Dm', 'Ebm', 'Em', 'Fm', 'F#m', 'Gm', 'G#m', 'Am', 'Bbm', 'Bm']
const keyOptions = computed(() => [
  { label: t('harmonyKeyAuto'), value: null },
  ...[...majorKeys, ...minorKeys].map((key) => ({ label: key, value: key })),
])

// ---- Instruments, so the preview can play the parts on the studio's hardware as on the Logic page -----------

const assignments = ref<Assignments>({})
void Promise.all([reloadInstruments(), listAssignments()])
  .then(([, stored]) => (assignments.value = stored))
  .catch(() => undefined)

async function assign(track: string, instrumentId: number | null): Promise<void> {
  const before = { ...assignments.value }
  const next = { ...assignments.value }
  if (instrumentId === null) {
    delete next[track]
  } else {
    next[track] = instrumentId
  }
  assignments.value = next
  try {
    await assignInstrument(track, instrumentId)
  } catch {
    assignments.value = before
  }
}

// ---- Generating ----------------------------------------------------------------------------------------

const source = computed<ScoreSource | null>(() => {
  if (mode.value === 'library') {
    return songId.value ? { song: songId.value } : null
  }
  return file.value ? { file: file.value, audio: null } : null
})
const result = ref<ConversionResult | null>(null)
const busy = ref(false)
const error = ref<string | null>(null)
/** The result belongs to the settings it was made with; a change marks it, as on the Logic page. */
const stale = ref(false)
watch([settings, source], () => (stale.value = result.value !== null), { deep: true })

/**
 * What the parts were derived from (key, guessed chords) and what went wrong, without the conversion's routine notes;
 * a melody alone has no chord track, which the MIDI reading would otherwise warn about.
 */
const notes = computed(() =>
  (result.value?.diagnostics ?? []).filter((d) =>
    d.severity === 'Info' ? ['YTL031', 'YTL032'].includes(d.code) : d.code !== 'YTL074',
  ),
)

async function generate(): Promise<void> {
  if (!source.value || settings.value.parts.length === 0) {
    return
  }
  busy.value = true
  error.value = null
  const form = {
    ...defaultFormState(),
    includeChords: settings.value.withChords,
    harmonyParts: settings.value.parts,
    harmonyKey: settings.value.key,
    harmonyChorusOnly: settings.value.chorusOnly,
  }
  try {
    result.value = await convertScore(
      source.value,
      withInstrumentChannels(toConversionOptions(form), assignments.value, instruments.value),
    )
    stale.value = false
  } catch (caught) {
    result.value = null
    error.value =
      caught instanceof ApiError && caught.status === 0
        ? t('networkError')
        : t('requestError', { message: caught instanceof Error ? caught.message : String(caught) })
  } finally {
    busy.value = false
  }
}

function fileName(): string {
  if (mode.value === 'upload' && file.value) {
    return `${baseName(file.value.name)}-begleitstimmen.mid`
  }
  const run = props.runs.find((r) => r.songs.some((s) => s.id === songId.value))
  return `${(run?.title || 'song').replace(/[\\/:*?"<>|]+/g, '-')}-begleitstimmen.mid`
}

function downloadMidi(): void {
  const blob = result.value ? midiBlob(result.value) : null
  if (blob) {
    download(blob, fileName())
  }
}
</script>

<template>
  <div class="grid grid-cols-[minmax(0,1fr)] gap-4">
    <Card>
      <template #title>
        <h2 class="m-0">{{ t('harmony') }}</h2>
      </template>
      <template #content>
        <p class="hint muted mt-0">{{ t('harmonyIntro') }}</p>
        <SelectButton
          v-model="mode"
          :options="modes"
          option-label="label"
          option-value="value"
          :allow-empty="false"
          class="mb-4"
        />
        <div v-show="mode === 'upload'">
          <FileDropZone
            :file="file"
            :extension="['.mid', '.midi', '.abc']"
            accept=".mid,.midi,.abc,audio/midi,audio/x-midi,text/plain"
            :drop-hint="t('harmonyDropHint')"
            :wrong-type-hint="t('notAbc')"
            @select="file = $event"
            @clear="file = null"
          />
        </div>
        <div v-show="mode === 'library'" class="flex flex-col gap-2">
          <Select
            v-model="songId"
            :options="songOptions"
            option-label="label"
            option-value="value"
            :placeholder="t('librarySongPick')"
            :empty-message="t('librarySongNone')"
            filter
            fluid
          />
        </div>

        <fieldset class="settings">
          <legend>{{ t('harmonyParts') }}</legend>
          <div class="parts">
            <div v-for="part in HARMONY_PARTS" :key="part" class="check">
              <Checkbox v-model="settings.parts" :value="part" :input-id="`harmony-${part}`" />
              <label :for="`harmony-${part}`">{{ t(partLabels[part]) }}</label>
            </div>
          </div>
          <div class="flex flex-wrap items-end gap-3">
            <label class="grid gap-1 flex-[1_1_10rem] text-sm text-muted-color">
              {{ t('harmonyKey') }}
              <Select
                v-model="settings.key"
                :options="keyOptions"
                option-label="label"
                option-value="value"
                :placeholder="t('harmonyKeyAuto')"
                fluid
              />
            </label>
          </div>
          <div class="check">
            <Checkbox v-model="settings.chorusOnly" binary input-id="harmony-chorus" />
            <label for="harmony-chorus">{{ t('harmonyChorusOnly') }}</label>
          </div>
          <div class="check">
            <Checkbox v-model="settings.withChords" binary input-id="harmony-chords" />
            <label for="harmony-chords">{{ t('harmonyWithChords') }}</label>
          </div>
        </fieldset>

        <div class="flex flex-wrap items-center gap-2 mt-4">
          <Button
            :label="busy ? t('harmonyGenerating') : t('harmonyGenerate')"
            icon="pi pi-users"
            :loading="busy"
            :disabled="!source || busy || settings.parts.length === 0"
            @click="generate"
          />
          <Button
            v-if="result?.success"
            :label="t('downloadMidi')"
            icon="pi pi-download"
            severity="secondary"
            :disabled="stale"
            @click="downloadMidi"
          />
          <Button :label="t('harmonyToLogic')" link @click="navigate('logic')" />
        </div>
        <p v-if="settings.parts.length === 0" class="hint muted">{{ t('harmonyNoParts') }}</p>
        <p v-if="stale" class="hint muted">{{ t('harmonyStale') }}</p>
        <p v-if="error" class="hint danger" role="alert">{{ error }}</p>
        <Message
          v-for="(note, index) in notes"
          :key="index"
          :severity="note.severity === 'Error' ? 'error' : note.severity === 'Warning' ? 'warn' : 'secondary'"
          size="small"
          variant="simple"
          class="mt-2"
        >
          {{ note.message }}
        </Message>
      </template>
    </Card>

    <ScorePreview
      v-if="result?.score"
      :score="result.score"
      :include-chords="settings.withChords"
      :stale="stale"
      :instruments="instruments"
      :assignments="assignments"
      :recording="null"
      @assign="assign"
      @manage-instruments="navigate('instruments')"
    />
  </div>
</template>

<style scoped>
.settings {
  display: grid;
  gap: 0.75rem;
  min-width: 0;
  margin: 1rem 0 0;
  padding: 0;
  border: 0;
}

legend {
  margin-bottom: 0.4rem;
  padding: 0;
  font-weight: 600;
}

.parts {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(9.5rem, 1fr));
  gap: 0.5rem 0.75rem;
}

.check {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
</style>
