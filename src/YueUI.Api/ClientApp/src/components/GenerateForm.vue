<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { ApiError, draftLyrics, generate } from '../api'
import { Panel } from 'primevue'
import {
  advancedChanged,
  resetAdvanced,
  defaultFormState,
  defaultSampling,
  exampleScore,
  lengthChoices,
  maxSongSeconds,
  toGenerateRequest,
  type FormState,
  type SamplingPhase,
} from '../form'
import { formatDuration, t } from '../i18n'
import type { LyricsState } from '../types'
import FieldHelp from './FieldHelp.vue'
import SamplingFields from './SamplingFields.vue'
import StyleBlocks from './StyleBlocks.vue'
import { Checkbox } from 'primevue'
import SelectButton from 'primevue/selectbutton'
import { InputNumber } from 'primevue'

const props = defineProps<{
  /** Whether the worker takes the extension's fields; null while unknown (no worker has started yet). */
  extensions: boolean | null
  /** YuE2 is generating, so there is no memory for the lyrics model. */
  busy: boolean
  /** The last lyrics draft from the event stream, from any browser. */
  lyricsDraft: LyricsState | null
}>()
const form = defineModel<FormState>({ required: true })

const sending = ref(false)
const blocksOpen = ref(false)
const message = ref<{ text: string; error: boolean } | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})

const changed = computed(() => advancedChanged(form.value))
// The API refuses this too; saying so before sending saves a round trip from the phone.
const scoreWithoutPlanning = computed(
  () => form.value.abc.trim() !== '' && form.value.cot === 'off' && !form.value.instrumental,
)
// The worker writes a score unless planning is off (an instrumental turns it back on) or one is supplied.
const plansScore = computed(() => (form.value.cot !== 'off' || form.value.instrumental) && form.value.abc.trim() === '')
// One field for the steps of the chosen quality; each keeps its own value.
const steps = computed({
  get: () => (form.value.quality === 'draft' ? form.value.draftSteps : form.value.fullSteps),
  set: (value: number) => {
    if (form.value.quality === 'draft') {
      form.value.draftSteps = value
    } else {
      form.value.fullSteps = value
    }
  },
})

async function submit(): Promise<void> {
  sending.value = true
  message.value = null
  fieldErrors.value = {}
  try {
    await generate(toGenerateRequest(form.value))
    message.value = { text: t('queued'), error: false }
  } catch (caught) {
    if (caught instanceof ApiError) {
      fieldErrors.value = caught.errors
    }
    message.value = { text: errorText(caught), error: true }
  } finally {
    sending.value = false
  }
}

const confirm = useConfirm()
const starting = ref(false)
const draftMessage = ref<{ text: string; error: boolean } | null>(null)
/** Whichever browser asked: while a draft is written there is no room for a second one, nor for a song. */
const drafting = computed(() => starting.value || props.lyricsDraft?.stage === 'writing')

/** The draft this browser asked for lands in the field when it arrives, also after a reload or a locked phone. */
function take(draft: LyricsState | null): void {
  if (!draft?.finished || draft.id !== form.value.lyricsDraftId) {
    return
  }
  form.value.lyricsDraftId = ''
  if (draft.stage === 'done' && draft.lyrics) {
    form.value.lyrics = draft.lyrics
    draftMessage.value = { text: t('lyricsDrafted'), error: false }
  } else {
    draftMessage.value = { text: t('errorGeneric', { message: draft.message ?? '' }), error: true }
  }
}

watch(() => props.lyricsDraft, take, { immediate: true })

/** A draft replaces the lyrics field; lyrics someone wrote by hand should not vanish without a question. */
function askDraft(): void {
  if (form.value.lyrics.trim() === '') {
    void draft()
    return
  }
  confirm.require({
    header: t('replaceLyrics'),
    message: t('confirmReplaceLyrics'),
    icon: 'pi pi-pencil',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('replace') },
    accept: () => void draft(),
  })
}

async function draft(): Promise<void> {
  starting.value = true
  draftMessage.value = null
  try {
    const started = await draftLyrics(form.value.lyricsIdea.trim(), form.value.style.trim())
    form.value.lyricsDraftId = started.id
    // A quick failure (LM Studio missing) can arrive as an event before this answer.
    take(props.lyricsDraft)
  } catch (caught) {
    draftMessage.value = { text: errorText(caught), error: true }
  } finally {
    starting.value = false
  }
}

function errorText(caught: unknown): string {
  if (caught instanceof ApiError && caught.status === 0) {
    return t('errorNetwork')
  }
  return t('errorGeneric', { message: caught instanceof Error ? caught.message : String(caught) })
}

function reset(): void {
  form.value = defaultFormState()
  message.value = null
  fieldErrors.value = {}
}

// App's trash button resets the form; message and field errors live here, so it calls this instead of the model.
defineExpose({ reset })

/** Parameters only: a score someone pasted or transcribed has its own button. */
function resetParameters(): void {
  form.value = resetAdvanced(form.value)
  fieldErrors.value = {}
}

function samplingChanged(phase: SamplingPhase): boolean {
  return JSON.stringify(form.value[phase]) !== JSON.stringify(defaultSampling[phase])
}

function resetSampling(phase: SamplingPhase): void {
  form.value = { ...form.value, [phase]: { ...defaultSampling[phase] } }
}

function lengthLabel(seconds: number): string {
  const duration = formatDuration(seconds)
  return seconds === maxSongSeconds ? t('defaultValue', { value: duration }) : duration
}

const cotItems = [
  { value: 'full', label: t('defaultValue', { value: t('cotFull') }) },
  { value: 'melody', label: t('cotMelody') },
  {
    value: 'off',
    label: t('cotOff'),
  },
]
const qualityOptions = [
  { value: 'draft', label: t('qualityDraft') },
  {
    value: 'full',
    label: t('qualityFull'),
  },
]
const batchOptions = [
  { value: 1, label: '1' },
  {
    value: 2,
    label: '2',
  },
  { value: 3, label: '3' },
]

const engineOptions = [
  {
    value: '',
    label: t('defaultValue', { value: t('enginesAuto') }),
  },
  {
    value: 'gpu',
    label: t('enginesGpu'),
  },
  {
    value: 'gpu+ane',
    label: t('enginesAne'),
  },
]
const lengthOptions = lengthChoices.map((x) => ({ value: x, label: lengthLabel(x) }))
</script>

<template>
  <form @submit.prevent="submit">
    <FloatLabel variant="on">
      <InputText
        id="gen-title"
        v-model="form.title"
        type="text"
        maxlength="120"
        :placeholder="t('titlePlaceholder')"
        aria-describedby="gen-title-help"
      />
      <label for="gen-title">{{ t('title') }}</label>
      <FieldHelp id="gen-title-help" :hint="t('titleHint')" />
    </FloatLabel>

    <FloatLabel variant="on" class="mt-6">
      <Textarea
        id="gen-style"
        v-model="form.style"
        rows="3"
        required
        :placeholder="t('stylePlaceholder')"
        aria-describedby="gen-style-help"
      />
      <label for="gen-style">{{ t('style') }}</label>
    </FloatLabel>
    <FieldHelp id="gen-style-help" :hint="t('styleHint')" :more="t('styleMore')" />
    <small v-if="fieldErrors.style" class="danger">{{ fieldErrors.style.join(' ') }}</small>
    <Button
      type="button"
      class="mt-1"
      size="small"
      text
      :icon="blocksOpen ? 'pi pi-chevron-up' : 'pi pi-th-large'"
      :label="t('styleBlocks')"
      :aria-expanded="blocksOpen"
      aria-controls="gen-style-blocks"
      @click="blocksOpen = !blocksOpen"
    />
    <StyleBlocks v-if="blocksOpen" id="gen-style-blocks" v-model="form.style" :instrumental="form.instrumental" />

    <div class="mt-6 flex items-start gap-2">
      <FloatLabel variant="on" class="min-w-0 flex-1">
        <InputText
          id="gen-lyrics-idea"
          v-model="form.lyricsIdea"
          type="text"
          maxlength="1000"
          :placeholder="t('lyricsIdeaPlaceholder')"
          aria-describedby="gen-lyrics-idea-help"
        />
        <label for="gen-lyrics-idea">{{ t('lyricsIdea') }}</label>
      </FloatLabel>
      <Button
        type="button"
        icon="pi pi-sparkles"
        text
        size="large"
        :loading="drafting"
        :disabled="drafting || busy || form.lyricsIdea.trim() === ''"
        v-tooltip.bottom="busy ? t('draftBusy') : undefined"
        @click="askDraft"
      />
    </div>
    <FieldHelp id="gen-lyrics-idea-help" :hint="t('lyricsIdeaHint')" :more="t('lyricsIdeaMore')" />
    <small v-if="draftMessage" :class="['block', draftMessage.error ? 'danger' : 'muted']" role="status">{{
      draftMessage.text
    }}</small>

    <FloatLabel variant="on" class="mt-6">
      <Textarea
        id="gen-lyrics"
        v-model="form.lyrics"
        class="mono"
        rows="12"
        :required="!form.instrumental"
        spellcheck="false"
        :placeholder="t('lyricsPlaceholder')"
        aria-describedby="gen-lyrics-help"
      />
      <label for="gen-lyrics">{{ form.instrumental ? t('lyricsOptional') : t('lyrics') }}</label>
      <FieldHelp id="gen-lyrics-help" :hint="t('lyricsHint')" :more="t('lyricsMore')" />
      <small v-if="fieldErrors.lyrics" class="danger">{{ fieldErrors.lyrics.join(' ') }}</small>
    </FloatLabel>

    <div class="flex items-center gap-2 mt-6">
      <Checkbox id="instrumental" binary v-model="form.instrumental" aria-describedby="gen-instrumental-help" />
      <label for="instrumental">{{ t('instrumental') }}</label>
    </div>
    <FieldHelp id="gen-instrumental-help" :hint="t('instrumentalHint')" :more="t('instrumentalMore')" />
    <Divider />
    <div class="grid grid-cols-2 gap-3">
      <div>
        <div>
          <label class="text-sm muted mb-0">{{ t('quality') }}</label
          ><br />
          <SelectButton v-model="form.quality" option-value="value" option-label="label" :options="qualityOptions" />
        </div>
        <FieldHelp id="gen-quality-help" :hint="t('qualityHint')" :more="t('qualityMore')" />
      </div>

      <div>
        <FloatLabel variant="on" class="min-w-0 flex-1 mt-5">
          <Select
            id="gen-batch"
            v-model.number="form.batch"
            option-label="label"
            option-value="value"
            :options="batchOptions"
            aria-describedby="gen-batch-help"
          />
          <label for="gen-batch">{{ t('batch') }}</label>
        </FloatLabel>
        <FieldHelp id="gen-batch-help" :hint="t('batchHint')" :more="t('batchMore')" />
        <small v-if="fieldErrors.batch" class="danger">{{ fieldErrors.batch.join(' ') }}</small>
      </div>
    </div>

    <Divider />
    <Panel toggleable class="mb-6 mt-4">
      <template #header>
        <div class="flex gap-4">
          {{ t('advanced') }}
        </div>
      </template>
      <template #icons>
        <Tag v-if="changed" severity="warn">{{ t('advancedChanged') }}</Tag>
        <Tag v-if="form.abc.trim() !== ''" severity="warn">{{ t('advancedWithScore') }}</Tag>
      </template>
      <div>
        <small class="muted">{{ t('advancedIntro') }}</small>
        <div class="flex justify-end">
          <Button
            icon="pi pi-refresh"
            :label="changed ? t('advancedReset') : t('advancedAtDefaults')"
            size="small"
            severity="warn"
            :disabled="!changed"
            @click="resetParameters"
          />
        </div>
      </div>
      <p v-if="extensions === false" class="notice" role="note">{{ t('extensionsOff') }}</p>

      <div class="grid grid-cols-2 gap-3">
        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <Select
              id="gen-cot"
              input-class="w-full"
              class="w-full"
              v-model="form.cot"
              :options="cotItems"
              option-label="label"
              option-value="value"
              aria-describedby="gen-cot-help"
            />
            <label for="gen-cot">{{ t('cot') }}</label>
          </FloatLabel>
          <small v-if="fieldErrors.cot" class="danger">{{ fieldErrors.cot.join(' ') }}</small>
          <FieldHelp id="gen-cot-help" :hint="t('cotHint')" :more="t('cotMore')" />
        </div>

        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <InputText
              id="gen-seed"
              v-model="form.seed"
              type="text"
              inputmode="numeric"
              pattern="[0-9]*"
              :placeholder="t('seedPlaceholder')"
              aria-describedby="gen-seed-help"
            />
            <label for="gen-seed">{{ t('seed') }}</label>
          </FloatLabel>

          <FieldHelp id="gen-seed-help" :hint="t('seedHint')" :more="t('seedMore')" />
          <small v-if="fieldErrors.seed" class="danger">{{ fieldErrors.seed.join(' ') }}</small>
        </div>

        <div>
          <FloatLabel variant="on" class="mt-6">
            <InputNumber
              id="gen-steps"
              v-model.number="steps"
              type="number"
              :min="1"
              :max="form.quality === 'draft' ? 32 : 64"
              :minFractionDigits="0"
              :pt="{
                pcInputText: {
                  root: {
                    inputmode: 'decimal',
                  },
                },
              }"
              aria-describedby="gen-steps-help"
            />
            <label for="gen-steps">{{ form.quality === 'draft' ? t('stepsDraft') : t('stepsFull') }}</label>
          </FloatLabel>
          <FieldHelp
            id="gen-steps-help"
            :hint="form.quality === 'draft' ? t('draftStepsHint') : t('fullStepsHint')"
            :more="t('stepsMore')"
          />
          <small v-if="fieldErrors.draftSteps || fieldErrors.fullSteps" class="danger">
            {{ (fieldErrors.draftSteps ?? fieldErrors.fullSteps)!.join(' ') }}
          </small>
        </div>
        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <Select
              id="gen-engines"
              v-model="form.engines"
              class="w-full"
              :options="engineOptions"
              option-value="value"
              option-label="label"
              aria-describedby="gen-engines-help"
            />
            <label for="gen-engines">{{ t('engines') }}</label>
          </FloatLabel>
          <FieldHelp id="gen-engines-help" :hint="t('enginesHint')" :more="t('enginesMore')" />
          <small v-if="fieldErrors.engines" class="danger">{{ fieldErrors.engines.join(' ') }}</small>
        </div>

        <div>
          <FloatLabel variant="on" class="mt-6 w-full">
            <Select
              option-label="label"
              option-value="value"
              class="w-full"
              id="gen-length"
              :options="lengthOptions"
              v-model.number="form.maxSeconds"
              aria-describedby="gen-length-help"
            />

            <label for="gen-length">{{ t('maxLength') }}</label>
          </FloatLabel>
          <FieldHelp id="gen-length-help" :hint="t('maxLengthHint')" :more="t('maxLengthMore')" />
          <small v-if="fieldErrors.maxTokens" class="danger">{{ fieldErrors.maxTokens.join(' ') }}</small>
        </div>

        <div class="col-span-2">
          <div class="flex gap-2">
            <div class="flex-1">
              <FloatLabel variant="on" class="mt-6">
                <Textarea
                  id="gen-abc"
                  v-model="form.abc"
                  class="mono"
                  :rows="8"
                  spellcheck="false"
                  autocapitalize="off"
                  autocomplete="off"
                  :placeholder="t('abcPlaceholder')"
                  aria-describedby="gen-abc-help"
                />
                <label for="gen-abc">{{ t('abc') }}</label>
              </FloatLabel>
              <FieldHelp id="gen-abc-help" :hint="t('abcHint')" :more="t('abcMore')" />
              <small v-if="scoreWithoutPlanning" class="danger">{{ t('abcNeedsPlanning') }}</small>
              <small v-else-if="fieldErrors.abc" class="danger">{{ fieldErrors.abc.join(' ') }}</small>
            </div>
            <div class="mt-4">
              <Button
                v-if="form.abc.trim() === ''"
                v-tooltip="t('abcExample')"
                icon="pi pi-upload"
                severity="info"
                text
                rounded
                @click="form.abc = exampleScore"
              />
              <Button v-else icon="pi pi-trash" @click="form.abc = ''" text rounded v-tooltip="t('abcClear')" />
            </div>
          </div>
        </div>

        <Panel class="col-span-2" toggleable>
          <template #header>
            {{ t('samplingSemantic') }}
          </template>
          <small class="muted">{{ t('samplingSemanticIntro') }}</small>
          <SamplingFields class="mt-6" v-model="form.semanticSampling" phase="semanticSampling" :errors="fieldErrors" />

          <div class="flex justify-end">
            <Button
              icon="pi pi-refresh"
              severity="warn"
              size="small"
              :label="samplingChanged('semanticSampling') ? t('samplingReset') : t('samplingAtDefaults')"
              :disabled="!samplingChanged('semanticSampling')"
              @click="resetSampling('semanticSampling')"
            />
          </div>
        </Panel>

        <Panel class="col-span-2" toggleable>
          <template #header>
            {{ t('samplingAbc') }}
          </template>
          <small class="muted">{{ t('samplingAbcIntro') }}</small>
          <small v-if="!plansScore" class="inactive">{{ t('samplingAbcInactive') }}</small>
          <SamplingFields
            class="mt-6"
            v-model="form.abcSampling"
            phase="abcSampling"
            :errors="fieldErrors"
            :disabled="!plansScore"
          />
          <div class="flex justify-end">
            <Button
              severity="warn"
              size="small"
              icon="pi pi-refresh"
              :disabled="!samplingChanged('abcSampling')"
              :label="samplingChanged('abcSampling') ? t('samplingReset') : t('samplingAtDefaults')"
              @click="resetSampling('abcSampling')"
            />
          </div>
        </Panel>
      </div>

      <template #footer>
        <div class="flex justify-end">
          <Button
            severity="warn"
            icon="pi pi-refresh"
            :label="changed ? t('advancedReset') : t('advancedAtDefaults')"
            size="small"
            :disabled="!changed"
            @click="resetParameters"
          />
        </div>
      </template>
    </Panel>

    <div>
      <Button
        type="submit"
        class="w-full"
        :label="sending ? t('generating') : t('generate')"
        severity="primary"
        :loading="sending || drafting || scoreWithoutPlanning"
        :disabled="sending || drafting || scoreWithoutPlanning"
      />
      <span v-if="message" :class="message.error ? 'danger' : 'muted'" role="status">{{ message.text }}</span>
    </div>
  </form>
</template>

<style scoped></style>
