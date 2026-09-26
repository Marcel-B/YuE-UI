<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import {
  ApiError,
  cancelTranscription,
  deleteTranscription,
  listTranscriptions,
  transcribe,
  transcriptionScore,
  transcriptionScoreUrl,
  transcriptionZipUrl,
} from '../api'
import { formatBytes, formatDateTime, t } from '../i18n'
import type { TranscriptionInfo, TranscriptionList, TranscriptionState, TranscriptionTask } from '../types'
import FieldHelp from './FieldHelp.vue'
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'

/** The live transcriptions from the event stream; the finished ones on disk are loaded here. */
const props = defineProps<{ transcriptions: TranscriptionState[] }>()
const emit = defineEmits<{ useScore: [abc: string, name: string]; error: [message: string] }>()

const file = ref<File | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)
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

const confirm = useConfirm()

function askDelete(item: TranscriptionInfo): void {
  confirm.require({
    header: t('confirmDelete'),
    message: t('confirmDeleteTranscription', { name: item.sourceName }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: async () => {
      try {
        await deleteTranscription(item.id)
        delete scores.value[item.id]
        await loadList()
      } catch (caught) {
        emit('error', caught instanceof Error ? caught.message : String(caught))
      }
    },
  })
}

const taskOptions = computed(() => [
  { value: 'melody-full', label: t('defaultValue', { value: t('taskFull') }) },
  { value: 'melody-vocal', label: t('taskVocal') },
])

function taskLabel(value: TranscriptionTask | null): string {
  return value === 'melody-vocal' ? t('taskVocal') : t('taskFull')
}
</script>

<template>
  <section class="flex flex-col gap-4">
    <FieldHelp id="transcribe-help" :hint="t('transcriptionIntro')" :more="t('transcriptionMore')" />

    <Message v-if="list && !list.installed" severity="warn" role="note">{{ t('transcriptionNotInstalled') }}</Message>

    <form class="flex flex-col gap-4" @submit.prevent="start">
      <div class="flex flex-col gap-1">
        <label for="transcribe-file" class="text-sm font-semibold">{{ t('recording') }}</label>
        <!-- The native field only opens the picker; the button and the name beside it are what shows. -->
        <input
          id="transcribe-file"
          ref="fileInput"
          type="file"
          accept="audio/*,.mp3,.m4a,.aac,.wav,.aif,.aiff,.flac,.caf"
          class="hidden"
          aria-describedby="transcribe-file-help"
          @change="pick"
        />
        <div class="flex min-w-0 items-center gap-2">
          <Button
            type="button"
            icon="pi pi-folder-open"
            :label="t('chooseRecording')"
            severity="secondary"
            outlined
            class="shrink-0"
            @click="fileInput?.click()"
          />
          <span class="muted min-w-0 truncate">{{ file?.name ?? t('noRecording') }}</span>
        </div>
        <FieldHelp id="transcribe-file-help" :hint="t('recordingHint')" />
      </div>

      <div class="flex flex-col gap-1">
        <label for="transcribe-task" class="text-sm font-semibold">{{ t('transcriptionTask') }}</label>
        <Select
          v-model="task"
          input-id="transcribe-task"
          :options="taskOptions"
          option-label="label"
          option-value="value"
          aria-describedby="transcribe-task-help"
        />
        <FieldHelp id="transcribe-task-help" :hint="t('taskHint')" :more="t('taskMore')" />
      </div>

      <div class="flex flex-wrap items-center gap-3">
        <Button
          type="submit"
          :label="sending ? t('uploading') : t('transcribe')"
          :loading="sending"
          :disabled="!file || sending || running !== null || list?.installed === false"
        />
        <span v-if="message" :class="message.error ? 'danger' : 'muted'" role="status">{{ message.text }}</span>
      </div>
    </form>

    <div v-if="running" class="flex flex-col gap-2">
      <div class="flex flex-wrap items-center gap-x-3">
        <strong class="min-w-0 break-words">{{ running.fileName }}</strong>
        <span class="muted">{{ t(`transcriptionStage_${running.stage}`) }}</span>
        <Button :label="t('cancel')" text size="small" severity="secondary" class="ml-auto" @click="cancel" />
      </div>
      <!-- Indeterminate while SheetSage2 only sends heartbeats: the bar then shows that it is busy. -->
      <ProgressBar
        :mode="running.fraction === null ? 'indeterminate' : 'determinate'"
        :value="Math.round((running.fraction ?? 0) * 100)"
        :show-value="false"
        class="h-2"
      />
      <small class="muted">{{ running.detail }}</small>
    </div>
    <p v-else-if="lastFailure" class="danger m-0">
      {{ lastFailure.fileName }}: {{ t(`transcriptionStage_${lastFailure.stage}`)
      }}{{ lastFailure.message ? ` – ${lastFailure.message}` : '' }}
    </p>

    <div>
      <h3 class="mt-2 mb-2 text-base">{{ t('transcriptions') }}</h3>
      <p v-if="list && list.items.length === 0" class="muted m-0">{{ t('transcriptionsEmpty') }}</p>
      <ul v-if="list" class="m-0 flex list-none flex-col gap-3 p-0">
        <li
          v-for="item in list.items"
          :key="item.id"
          class="flex flex-col gap-1 border-t border-surface pt-3 first:border-t-0 first:pt-0"
        >
          <div class="flex flex-wrap items-center gap-x-3">
            <strong class="min-w-0 break-words">{{ item.sourceName }}</strong>
            <span class="muted">{{ taskLabel(item.task) }}</span>
            <span v-if="item.createdAt" class="muted">{{ formatDateTime(item.createdAt) }}</span>
            <span class="muted">{{ formatBytes(item.bytes) }}</span>
          </div>
          <small v-if="item.warnings.length" class="muted">{{
            t('warnings', { list: item.warnings.join('; ') })
          }}</small>
          <div class="flex flex-wrap items-center">
            <Button :label="t('useScore')" text size="small" @click="useScore(item)" />
            <Button as="a" :label="t('score')" text size="small" :href="transcriptionScoreUrl(item.id)" />
            <Button
              as="a"
              :label="t('files')"
              text
              size="small"
              :href="transcriptionZipUrl(item.id)"
              :title="t('filesTitle')"
            />
            <Button
              icon="pi pi-trash"
              text
              rounded
              size="small"
              severity="danger"
              class="ml-auto"
              v-tooltip="t('deleteTranscription')"
              :aria-label="t('deleteTranscription')"
              @click="askDelete(item)"
            />
          </div>
          <details class="text-sm" @toggle="toggleScore(item, $event)">
            <summary class="cursor-pointer text-primary">{{ t('showScore') }}</summary>
            <pre class="score">{{ scores[item.id] ?? '…' }}</pre>
          </details>
        </li>
      </ul>
    </div>
  </section>
</template>

<style scoped>
.score {
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
</style>
