<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  ApiError,
  cancelTranscription,
  listTranscriptions,
  transcribe,
  transcriptionScore,
  transcriptionScoreUrl,
  transcriptionZipUrl,
} from '../api'
import { formatDateTime, t } from '../i18n'
import type { TranscriptionInfo, TranscriptionList, TranscriptionState, TranscriptionTask } from '../types'
import FieldHelp from './FieldHelp.vue'

/** The live transcriptions from the event stream; the finished ones on disk are loaded here. */
const props = defineProps<{ transcriptions: TranscriptionState[] }>()
const emit = defineEmits<{ useScore: [abc: string, name: string]; error: [message: string] }>()

const file = ref<File | null>(null)
const task = ref<TranscriptionTask>('melody-full')
const sending = ref(false)
const message = ref<{ text: string; error: boolean } | null>(null)
const list = ref<TranscriptionList | null>(null)
/** Scores opened with "View ABC", by transcription id. */
const scores = ref<Record<string, string>>({})

const running = computed(() => props.transcriptions.find((tr) => !tr.finished) ?? null)
// The last one that ended without a score, so its reason stays visible until the next start.
const lastFailure = computed(() => {
  const last = props.transcriptions.at(-1)
  return last && (last.stage === 'failed' || last.stage === 'cancelled') ? last : null
})

async function loadList(): Promise<void> {
  try {
    list.value = await listTranscriptions()
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

onMounted(loadList)
// A transcription that just finished belongs in the list.
watch(
  () => props.transcriptions.filter((tr) => tr.stage === 'done').length,
  () => void loadList(),
)

function pick(event: Event): void {
  file.value = (event.target as HTMLInputElement).files?.[0] ?? null
}

async function start(): Promise<void> {
  if (!file.value) {
    return
  }
  sending.value = true
  message.value = null
  try {
    await transcribe(file.value, task.value)
    message.value = { text: t('transcriptionStarted'), error: false }
  } catch (caught) {
    message.value = {
      text: caught instanceof ApiError && caught.status === 0 ? t('errorNetwork') : String((caught as Error).message),
      error: true,
    }
    if (caught instanceof ApiError && caught.status === 503) {
      void loadList()
    }
  } finally {
    sending.value = false
  }
}

async function cancel(): Promise<void> {
  if (running.value) {
    try {
      await cancelTranscription(running.value.id)
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  }
}

async function score(item: TranscriptionInfo): Promise<string> {
  scores.value[item.id] ??= await transcriptionScore(item.id)
  return scores.value[item.id]!
}

async function useScore(item: TranscriptionInfo): Promise<void> {
  try {
    emit('useScore', await score(item), item.sourceName)
  } catch (caught) {
    emit('error', caught instanceof Error ? caught.message : String(caught))
  }
}

async function toggleScore(item: TranscriptionInfo, event: Event): Promise<void> {
  if ((event.target as HTMLDetailsElement).open && !scores.value[item.id]) {
    try {
      await score(item)
    } catch (caught) {
      emit('error', caught instanceof Error ? caught.message : String(caught))
    }
  }
}

function taskLabel(value: TranscriptionTask | null): string {
  return value === 'melody-vocal' ? t('taskVocal') : t('taskFull')
}
</script>

<template>
  <section>
    <FieldHelp id="transcribe-help" :hint="t('transcriptionIntro')" :more="t('transcriptionMore')" />

    <p v-if="list && !list.installed" class="notice" role="note">{{ t('transcriptionNotInstalled') }}</p>

    <form class="start" @submit.prevent="start">
      <div class="field">
        <label for="transcribe-file">{{ t('recording') }}</label>
        <input
          id="transcribe-file"
          type="file"
          accept="audio/*,.mp3,.m4a,.aac,.wav,.aif,.aiff,.flac,.caf"
          aria-describedby="transcribe-file-help"
          @change="pick"
        />
        <FieldHelp id="transcribe-file-help" :hint="t('recordingHint')" />
      </div>

      <div class="field">
        <label for="transcribe-task">{{ t('transcriptionTask') }}</label>
        <select id="transcribe-task" v-model="task" aria-describedby="transcribe-task-help">
          <option value="melody-full">{{ t('defaultValue', { value: t('taskFull') }) }}</option>
          <option value="melody-vocal">{{ t('taskVocal') }}</option>
        </select>
        <FieldHelp id="transcribe-task-help" :hint="t('taskHint')" :more="t('taskMore')" />
      </div>

      <div class="actions">
        <button
          type="submit"
          class="button primary"
          :disabled="!file || sending || running !== null || list?.installed === false"
        >
          {{ sending ? t('uploading') : t('transcribe') }}
        </button>
        <span v-if="message" :class="message.error ? 'danger' : 'muted'" role="status">{{ message.text }}</span>
      </div>
    </form>

    <div v-if="running" class="running">
      <div class="line">
        <strong>{{ running.fileName }}</strong>
        <span class="muted">{{ t(`transcriptionStage_${running.stage}`) }}</span>
        <button type="button" class="link push" @click="cancel">{{ t('cancel') }}</button>
      </div>
      <!-- No value while SheetSage2 only sends heartbeats: the bar then shows that it is busy. -->
      <progress :value="running.fraction ?? undefined" max="1" />
      <small class="muted">{{ running.detail }}</small>
    </div>
    <p v-else-if="lastFailure" class="danger failure">
      {{ lastFailure.fileName }}: {{ t(`transcriptionStage_${lastFailure.stage}`)
      }}{{ lastFailure.message ? ` – ${lastFailure.message}` : '' }}
    </p>

    <h3>{{ t('transcriptions') }}</h3>
    <p v-if="list && list.items.length === 0" class="muted">{{ t('transcriptionsEmpty') }}</p>
    <ul v-if="list" class="items">
      <li v-for="item in list.items" :key="item.id" class="item">
        <div class="line">
          <strong class="name">{{ item.sourceName }}</strong>
          <span class="muted">{{ taskLabel(item.task) }}</span>
          <span v-if="item.createdAt" class="muted">{{ formatDateTime(item.createdAt) }}</span>
        </div>
        <small v-if="item.warnings.length" class="muted">{{ t('warnings', { list: item.warnings.join('; ') }) }}</small>
        <div class="line links">
          <button type="button" class="link" @click="useScore(item)">{{ t('useScore') }}</button>
          <a class="link" :href="transcriptionScoreUrl(item.id)">{{ t('score') }}</a>
          <a class="link" :href="transcriptionZipUrl(item.id)" :title="t('filesTitle')">{{ t('files') }}</a>
        </div>
        <details @toggle="toggleScore(item, $event)">
          <summary>{{ t('showScore') }}</summary>
          <pre>{{ scores[item.id] ?? '…' }}</pre>
        </details>
      </li>
    </ul>
  </section>
</template>

<style scoped></style>
