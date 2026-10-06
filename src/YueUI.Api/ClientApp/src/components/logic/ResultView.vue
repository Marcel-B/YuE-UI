<script setup lang="ts">
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'
import { computed, ref } from 'vue'
import { formatDuration, formatNumber, t, type MessageKey } from '../../logic/i18n'
import { barAt, barCount, download, jsonBlob, midiBlob } from '../../logic/score'
import type { LogicProgress } from '../../logic/api'
import { logicNotes, type LogicNote } from '../../logic/logicNotes'
import type { ConversionResult, Diagnostic, ScoreDocument } from '../../logic/types'

const props = defineProps<{
  result: ConversionResult
  outputName: string
  stale: boolean
  hasAudio: boolean
  logicBusy: boolean
  logicProgress: LogicProgress | null
  logicError: string | null
  logicWarnings: Diagnostic[]
  musicXmlBusy: boolean
  musicXmlError: string | null
}>()

const logicNote = computed(() => (props.hasAudio ? t('logicHint') : t('logicWithoutAudio')))
const emit = defineEmits<{ exportLogic: []; exportMusicXml: [] }>()

/** Percent of the upload or download, or null while building or when the size is unknown. */
const logicPercent = computed(() => {
  const progress = props.logicProgress
  return progress && progress.phase !== 'build' && progress.total
    ? Math.floor((progress.loaded / progress.total) * 100)
    : null
})

/** What the export is doing, so a long wait for a big FLAC does not look stuck. */
const logicLabel = computed(() => {
  const progress = props.logicProgress
  if (!props.logicBusy || !progress) {
    return props.logicBusy ? t('buildingLogic') : t('downloadLogic')
  }
  if (progress.phase === 'build') {
    return t('buildingLogic')
  }
  const key = progress.phase === 'upload' ? 'uploadingLogic' : 'receivingLogic'
  return logicPercent.value !== null
    ? t(key, { amount: `${logicPercent.value} %` })
    : t(key, { amount: `${formatNumber(progress.loaded / (1024 * 1024))} MB` })
})

const showInfos = ref(false)

const notes = computed(() => logicNotes(props.logicWarnings))

function noteText(note: LogicNote): string {
  switch (note.kind) {
    case 'midiOnly':
      return t('logicMidiOnly', { voices: note.voices.join(', ') })
    case 'unknownOutput':
      return t('logicUnknownOutput', { track: note.track, port: note.port, instrument: note.instrument })
    case 'other':
      return note.diagnostic.message
  }
}

const score = computed(() => props.result.score)
const important = computed(() => props.result.diagnostics.filter((d) => d.severity !== 'Info'))
const infos = computed(() => props.result.diagnostics.filter((d) => d.severity === 'Info'))

/** Section widths for the timeline strip, in percent of the song length. */
const timeline = computed(() => {
  const doc = score.value
  if (!doc || doc.sections.length === 0 || doc.lengthTicks === 0) {
    return []
  }
  return doc.sections.map((section, index) => {
    const end = doc.sections[index + 1]?.startTicks ?? doc.lengthTicks
    return {
      name: section.name,
      bar: barAt(doc, section.startTicks),
      width: ((end - section.startTicks) / doc.lengthTicks) * 100,
    }
  })
})

const tracks = computed(() => {
  const doc = score.value
  if (!doc) {
    return []
  }
  const melodies = doc.voices
    .filter((v) => v.kind === 'Melody')
    .map((v) => ({ name: v.id, detail: t('notes', { count: v.notes.length }) }))
  const generated = doc.voices
    .filter((v) => v.kind !== 'Melody')
    .map((v) => ({ name: v.id, detail: t('notes', { count: v.notes.length }) }))
  const chords =
    doc.chords.length > 0 ? [{ name: t('chordTrack'), detail: t('chords', { count: doc.chords.length }) }] : []
  return [...melodies, ...chords, ...generated]
})

function meters(doc: ScoreDocument): string {
  return doc.timeSignatures.map((s) => `${s.numerator}/${s.denominator}`).join(', ')
}

function severityLabel(diagnostic: Diagnostic): string {
  return t(`severity_${diagnostic.severity}` as MessageKey)
}

function location(diagnostic: Diagnostic): string {
  if (diagnostic.line === null) {
    return ''
  }
  return diagnostic.column === null
    ? t('location', { line: diagnostic.line })
    : t('locationColumn', { line: diagnostic.line, column: diagnostic.column })
}

function downloadMidi(): void {
  const blob = midiBlob(props.result)
  if (blob) {
    download(blob, `${props.outputName}.mid`)
  }
}

function downloadJson(): void {
  download(jsonBlob(props.result), `${props.outputName}.json`)
}
</script>

<template>
  <Card aria-live="polite">
    <template #title>
      <span :class="{ 'text-(--danger)': !result.success }">
        {{ result.success ? t('resultTitle') : t('failedTitle') }}
      </span>
    </template>
    <template #content>
      <Message v-if="stale" severity="warn" size="small">{{ t('stale') }}</Message>

      <template v-if="score">
        <dl class="facts">
          <div>
            <dt>{{ t('tempo') }}</dt>
            <dd>{{ formatNumber(score.tempoBpm, 2) }} BPM</dd>
          </div>
          <div>
            <dt>{{ t('meter') }}</dt>
            <dd>{{ meters(score) }}</dd>
          </div>
          <div>
            <dt>{{ t('key') }}</dt>
            <dd>{{ score.keySignatures.map((k) => k.key).join(', ') }}</dd>
          </div>
          <div>
            <dt>{{ t('length') }}</dt>
            <dd>{{ t('lengthValue', { bars: barCount(score), duration: formatDuration(score.durationSeconds) }) }}</dd>
          </div>
        </dl>

        <h3>{{ t('sections') }}</h3>
        <div v-if="timeline.length" class="timeline" role="list">
          <div
            v-for="(section, index) in timeline"
            :key="index"
            class="segment"
            role="listitem"
            :style="{ flexGrow: section.width }"
            :title="`${section.name} · ${t('bar', { bar: section.bar })}`"
          >
            <span class="segment-name">{{ section.name }}</span>
            <span class="segment-bar">{{ section.bar }}</span>
          </div>
        </div>
        <p v-else class="muted">{{ t('noSections') }}</p>

        <h3>{{ t('trackList') }}</h3>
        <ul class="tracks">
          <li v-for="track in tracks" :key="track.name">
            <span>{{ track.name }}</span>
            <span class="muted">{{ track.detail }}</span>
          </li>
        </ul>

        <div class="flex flex-wrap gap-2 mt-5">
          <Button :label="t('downloadMidi')" icon="pi pi-download" :disabled="!result.midi" @click="downloadMidi" />
          <Button
            :label="t('downloadJson')"
            icon="pi pi-download"
            severity="secondary"
            outlined
            @click="downloadJson"
          />
          <Button
            :label="musicXmlBusy ? t('buildingMusicXml') : t('downloadMusicXml')"
            icon="pi pi-file"
            severity="secondary"
            outlined
            :loading="musicXmlBusy"
            :disabled="musicXmlBusy"
            @click="emit('exportMusicXml')"
          />
          <Button
            :label="logicLabel"
            icon="pi pi-box"
            severity="secondary"
            outlined
            :loading="logicBusy"
            :disabled="logicBusy"
            @click="emit('exportLogic')"
          />
        </div>
        <ProgressBar
          v-if="logicBusy && logicPercent !== null"
          :value="logicPercent"
          :show-value="false"
          class="logic-progress"
          :aria-label="logicLabel"
        />
        <p class="hint muted">{{ logicNote }}</p>
        <p class="hint muted">{{ t('musicXmlHint') }}</p>
        <p v-if="logicError" class="hint danger" role="alert">{{ logicError }}</p>
        <p v-if="musicXmlError" class="hint danger" role="alert">{{ musicXmlError }}</p>
        <div v-if="logicWarnings.length" class="logic-warnings">
          <h3>{{ t('logicWarnings') }}</h3>
          <ul class="diagnostics">
            <li
              v-for="(note, index) in notes"
              :key="index"
              :class="note.kind === 'other' ? note.diagnostic.severity.toLowerCase() : 'warning'"
            >
              <span class="message">{{ noteText(note) }}</span>
            </li>
          </ul>
        </div>
      </template>

      <h3>{{ t('diagnostics') }}</h3>
      <p v-if="result.diagnostics.length === 0" class="muted">{{ t('noDiagnostics') }}</p>
      <ul v-if="important.length" class="diagnostics">
        <li v-for="(diagnostic, index) in important" :key="index" :class="diagnostic.severity.toLowerCase()">
          <span class="badge">{{ severityLabel(diagnostic) }}</span>
          <span class="code">{{ diagnostic.code }}</span>
          <span v-if="location(diagnostic)" class="muted">{{ location(diagnostic) }}</span>
          <span class="message">{{ diagnostic.message }}</span>
        </li>
      </ul>
      <template v-if="infos.length">
        <Button
          v-if="!showInfos"
          :label="t('showInfos', { count: infos.length })"
          link
          size="small"
          class="px-0"
          @click="showInfos = true"
        />
        <ul v-else class="diagnostics">
          <li v-for="(diagnostic, index) in infos" :key="index" class="info">
            <span class="badge">{{ severityLabel(diagnostic) }}</span>
            <span class="code">{{ diagnostic.code }}</span>
            <span v-if="location(diagnostic)" class="muted">{{ location(diagnostic) }}</span>
            <span class="message">{{ diagnostic.message }}</span>
          </li>
        </ul>
      </template>
    </template>
  </Card>
</template>

<style scoped>
.logic-progress {
  height: 0.375rem;
  margin-top: 0.75rem;
}

h3 {
  margin: 1.25rem 0 0.5rem;
  font-size: 0.95rem;
}

.facts {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(9rem, 1fr));
  gap: 0.75rem;
  margin: 0;
}

.facts div {
  padding: 0.6rem 0.75rem;
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
}

dt {
  color: var(--text-muted);
  font-size: 0.8rem;
}

dd {
  margin: 0.15rem 0 0;
  font-size: 1.1rem;
  font-weight: 600;
  font-variant-numeric: tabular-nums;
}

.timeline {
  display: flex;
  gap: 2px;
  overflow: hidden;
  border-radius: var(--radius-small);
}

.segment {
  display: flex;
  flex-basis: 0;
  flex-direction: column;
  min-width: 2.5rem;
  padding: 0.4rem 0.5rem;
  overflow: hidden;
  background: var(--accent-soft);
  color: var(--text);
  font-size: 0.8rem;
}

.segment:nth-child(even) {
  background: var(--surface-sunken);
}

.segment-name {
  overflow: hidden;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.segment-bar {
  color: var(--text-muted);
  font-variant-numeric: tabular-nums;
}

.tracks {
  display: grid;
  gap: 0.25rem;
  margin: 0;
  padding: 0;
  list-style: none;
}

.tracks li {
  display: flex;
  justify-content: space-between;
  gap: 1rem;
  padding: 0.35rem 0;
  border-bottom: 1px solid var(--border);
}

.actions {
  display: flex;
  flex-wrap: wrap;
  gap: 0.75rem;
  margin-top: 1.25rem;
}

.diagnostics {
  display: grid;
  gap: 0.5rem;
  margin: 0 0 0.5rem;
  padding: 0;
  list-style: none;
}

.diagnostics li {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 0.25rem 0.6rem;
  padding: 0.5rem 0.75rem;
  border-left: 3px solid var(--border-strong);
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
  font-size: 0.9rem;
}

.diagnostics .error {
  border-left-color: var(--danger);
}

.diagnostics .warning {
  border-left-color: var(--warning);
}

.badge {
  font-weight: 600;
}

.error .badge {
  color: var(--danger);
}

.warning .badge {
  color: var(--warning-text);
}

.code {
  font-family: var(--font-mono);
  font-size: 0.8rem;
}

.message {
  flex-basis: 100%;
}
</style>
