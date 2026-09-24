<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { ApiError, draftLyrics, generate } from '../api'
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
      <FieldHelp id="gen-style-help" :hint="t('styleHint')" :more="t('styleMore')" />
      <label for="gen-style">{{ t('style') }}</label>
      <small v-if="fieldErrors.style" class="danger">{{ fieldErrors.style.join(' ') }}</small>
    </FloatLabel>

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
        :label="drafting ? t('draftingLyrics') : t('draftLyrics')"
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

    <div class="field mt-6">
      <label class="check">
        <input v-model="form.instrumental" type="checkbox" aria-describedby="gen-instrumental-help" />
        <span>{{ t('instrumental') }}</span>
      </label>
      <FieldHelp id="gen-instrumental-help" :hint="t('instrumentalHint')" :more="t('instrumentalMore')" />
    </div>

    <div class="grid">
      <div class="field">
        <fieldset class="segmented" aria-describedby="gen-quality-help">
          <legend>{{ t('quality') }}</legend>
          <label :class="{ active: form.quality === 'draft' }">
            <input v-model="form.quality" type="radio" value="draft" class="sr-only" />{{ t('qualityDraft') }}
          </label>
          <label :class="{ active: form.quality === 'full' }">
            <input v-model="form.quality" type="radio" value="full" class="sr-only" />{{ t('qualityFull') }}
          </label>
        </fieldset>
        <FieldHelp id="gen-quality-help" :hint="t('qualityHint')" :more="t('qualityMore')" />
      </div>

      <div class="field">
        <label for="gen-batch">{{ t('batch') }}</label>
        <select id="gen-batch" v-model.number="form.batch" class="compact" aria-describedby="gen-batch-help">
          <option v-for="n in 4" :key="n" :value="n">{{ n }}</option>
        </select>
        <FieldHelp id="gen-batch-help" :hint="t('batchHint')" :more="t('batchMore')" />
        <small v-if="fieldErrors.batch" class="danger">{{ fieldErrors.batch.join(' ') }}</small>
      </div>
    </div>

    <details class="advanced">
      <summary>
        {{ t('advanced') }}
        <span v-if="changed" class="badge changed">{{ t('advancedChanged') }}</span>
        <span v-if="form.abc.trim() !== ''" class="badge changed">{{ t('advancedWithScore') }}</span>
      </summary>
      <div class="advanced-head">
        <small class="muted">{{ t('advancedIntro') }}</small>
        <button type="button" class="button secondary small" :disabled="!changed" @click="resetParameters">
          {{ changed ? t('advancedReset') : t('advancedAtDefaults') }}
        </button>
      </div>
      <p v-if="extensions === false" class="notice" role="note">{{ t('extensionsOff') }}</p>

      <div class="grid">
        <div class="field">
          <label for="gen-cot">{{ t('cot') }}</label>
          <select id="gen-cot" v-model="form.cot" aria-describedby="gen-cot-help">
            <option value="full">{{ t('defaultValue', { value: t('cotFull') }) }}</option>
            <option value="melody">{{ t('cotMelody') }}</option>
            <option value="off">{{ t('cotOff') }}</option>
          </select>
          <FieldHelp id="gen-cot-help" :hint="t('cotHint')" :more="t('cotMore')" />
          <small v-if="fieldErrors.cot" class="danger">{{ fieldErrors.cot.join(' ') }}</small>
        </div>

        <div class="field">
          <label for="gen-seed">{{ t('seed') }}</label>
          <input
            id="gen-seed"
            v-model="form.seed"
            type="text"
            inputmode="numeric"
            pattern="[0-9]*"
            :placeholder="t('seedPlaceholder')"
            aria-describedby="gen-seed-help"
          />
          <FieldHelp id="gen-seed-help" :hint="t('seedHint')" :more="t('seedMore')" />
          <small v-if="fieldErrors.seed" class="danger">{{ fieldErrors.seed.join(' ') }}</small>
        </div>

        <div class="field">
          <label for="gen-steps">{{ form.quality === 'draft' ? t('stepsDraft') : t('stepsFull') }}</label>
          <input
            id="gen-steps"
            v-model.number="steps"
            type="number"
            min="1"
            :max="form.quality === 'draft' ? 32 : 64"
            aria-describedby="gen-steps-help"
          />
          <FieldHelp
            id="gen-steps-help"
            :hint="form.quality === 'draft' ? t('draftStepsHint') : t('fullStepsHint')"
            :more="t('stepsMore')"
          />
          <small v-if="fieldErrors.draftSteps || fieldErrors.fullSteps" class="danger">
            {{ (fieldErrors.draftSteps ?? fieldErrors.fullSteps)!.join(' ') }}
          </small>
        </div>

        <div class="field">
          <label for="gen-engines">{{ t('engines') }}</label>
          <select id="gen-engines" v-model="form.engines" aria-describedby="gen-engines-help">
            <option value="">{{ t('defaultValue', { value: t('enginesAuto') }) }}</option>
            <option value="gpu">{{ t('enginesGpu') }}</option>
            <option value="gpu+ane">{{ t('enginesAne') }}</option>
          </select>
          <FieldHelp id="gen-engines-help" :hint="t('enginesHint')" :more="t('enginesMore')" />
          <small v-if="fieldErrors.engines" class="danger">{{ fieldErrors.engines.join(' ') }}</small>
        </div>

        <div class="field">
          <label for="gen-length">{{ t('maxLength') }}</label>
          <select id="gen-length" v-model.number="form.maxSeconds" aria-describedby="gen-length-help">
            <option v-for="seconds in lengthChoices" :key="seconds" :value="seconds">{{ lengthLabel(seconds) }}</option>
          </select>
          <FieldHelp id="gen-length-help" :hint="t('maxLengthHint')" :more="t('maxLengthMore')" />
          <small v-if="fieldErrors.maxTokens" class="danger">{{ fieldErrors.maxTokens.join(' ') }}</small>
        </div>

        <div class="field wide">
          <div class="label-row">
            <label for="gen-abc">{{ t('abc') }}</label>
            <button v-if="form.abc.trim() === ''" type="button" class="link" @click="form.abc = exampleScore">
              {{ t('abcExample') }}
            </button>
            <button v-else type="button" class="link" @click="form.abc = ''">{{ t('abcClear') }}</button>
          </div>
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
          <FieldHelp id="gen-abc-help" :hint="t('abcHint')" :more="t('abcMore')" />
          <small v-if="scoreWithoutPlanning" class="danger">{{ t('abcNeedsPlanning') }}</small>
          <small v-else-if="fieldErrors.abc" class="danger">{{ fieldErrors.abc.join(' ') }}</small>
        </div>

        <details class="sampling wide">
          <summary>{{ t('samplingSemantic') }}</summary>
          <small class="muted">{{ t('samplingSemanticIntro') }}</small>
          <SamplingFields v-model="form.semanticSampling" phase="semanticSampling" :errors="fieldErrors" />
          <button
            type="button"
            class="button secondary small group-reset"
            :disabled="!samplingChanged('semanticSampling')"
            @click="resetSampling('semanticSampling')"
          >
            {{ samplingChanged('semanticSampling') ? t('samplingReset') : t('samplingAtDefaults') }}
          </button>
        </details>

        <details class="sampling wide">
          <summary>{{ t('samplingAbc') }}</summary>
          <small class="muted">{{ t('samplingAbcIntro') }}</small>
          <small v-if="!plansScore" class="inactive">{{ t('samplingAbcInactive') }}</small>
          <SamplingFields
            v-model="form.abcSampling"
            phase="abcSampling"
            :errors="fieldErrors"
            :disabled="!plansScore"
          />
          <button
            type="button"
            class="button secondary small group-reset"
            :disabled="!samplingChanged('abcSampling')"
            @click="resetSampling('abcSampling')"
          >
            {{ samplingChanged('abcSampling') ? t('samplingReset') : t('samplingAtDefaults') }}
          </button>
        </details>
      </div>

      <!-- The section is long on a phone: the same reset at its end. -->
      <div class="advanced-foot">
        <button type="button" class="button secondary small" :disabled="!changed" @click="resetParameters">
          {{ changed ? t('advancedReset') : t('advancedAtDefaults') }}
        </button>
      </div>
    </details>

    <div class="actions">
      <button type="submit" class="button primary" :disabled="sending || drafting || scoreWithoutPlanning">
        {{ sending ? t('generating') : t('generate') }}
      </button>
      <span v-if="message" :class="message.error ? 'danger' : 'muted'" role="status">{{ message.text }}</span>
    </div>
  </form>
</template>

<style scoped>
.generate {
  display: flex;
  flex-direction: column;
  gap: 0.9rem;
}

.heading {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
}

.heading h2 {
  margin: 0;
}

.mono {
  font-family: var(--font-mono);
  font-size: 0.9rem;
}

.compact {
  width: 6rem;
}

.segmented {
  display: inline-flex;
  margin: 0;
  padding: 0;
  border: 0;
}

.segmented legend {
  margin-bottom: 0.3rem;
  padding: 0;
  font-size: 0.9rem;
  font-weight: 600;
}

.segmented label {
  padding: 0.45rem 1rem;
  border: 1px solid var(--border-strong);
  background: var(--surface);
  cursor: pointer;
}

.segmented label:first-of-type {
  border-radius: var(--radius-small) 0 0 var(--radius-small);
}

.segmented label:last-of-type {
  margin-left: -1px;
  border-radius: 0 var(--radius-small) var(--radius-small) 0;
}

.segmented label.active {
  border-color: var(--accent);
  background: var(--accent-soft);
  color: var(--accent);
  font-weight: 600;
}

.segmented label:focus-within {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.advanced {
  padding-top: 0.9rem;
  border-top: 1px solid var(--border);
}

.advanced summary {
  cursor: pointer;
  font-weight: 600;
}

.changed {
  margin-left: 0.4rem;
  background: var(--warning-soft);
  color: var(--warning-text);
}

.advanced-foot {
  display: flex;
  justify-content: flex-end;
  margin-top: 1rem;
}

.group-reset {
  margin-top: 0.9rem;
}

.advanced-head {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  justify-content: space-between;
  gap: 0.5rem 1rem;
  margin-top: 0.6rem;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr));
  align-items: start;
  gap: 1.1rem 1rem;
}

.advanced .grid {
  margin-top: 0.9rem;
}

.wide {
  grid-column: 1 / -1;
}

.notice {
  margin: 0.75rem 0 0;
  padding: 0.6rem 0.75rem;
  border-radius: var(--radius-small);
  background: var(--warning-soft);
  color: var(--warning-text);
  font-size: 0.85rem;
}

.sampling {
  padding: 0.75rem;
  border: 1px solid var(--border);
  border-radius: var(--radius-small);
}

.sampling summary {
  cursor: pointer;
  font-size: 0.95rem;
  font-weight: 600;
}

.sampling > small {
  display: block;
  margin-top: 0.5rem;
  font-size: 0.8rem;
}

.inactive {
  color: var(--warning-text);
}

.label-row {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  gap: 1rem;
}

.label-row label {
  font-size: 0.9rem;
  font-weight: 600;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 1rem;
}
</style>
