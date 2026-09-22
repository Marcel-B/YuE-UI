<script setup lang="ts">
import { ref } from 'vue'
import { ApiError, generate } from '../api'
import { defaultFormState, toGenerateRequest, type FormState } from '../form'
import { t } from '../i18n'

const form = defineModel<FormState>({ required: true })

const sending = ref(false)
const message = ref<{ text: string; error: boolean } | null>(null)
const fieldErrors = ref<Record<string, string[]>>({})

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
</script>

<template>
  <form class="card generate" @submit.prevent="submit">
    <div class="heading">
      <h2>{{ t('newSong') }}</h2>
      <button type="button" class="link" @click="reset">{{ t('resetForm') }}</button>
    </div>

    <label class="field">
      <span>{{ t('title') }}</span>
      <input v-model="form.title" type="text" maxlength="120" :placeholder="t('titlePlaceholder')" />
    </label>

    <label class="field">
      <span>{{ t('style') }}</span>
      <textarea v-model="form.style" rows="3" required :placeholder="t('stylePlaceholder')" />
      <small v-if="fieldErrors.style" class="danger">{{ fieldErrors.style.join(' ') }}</small>
    </label>

    <label class="field">
      <span>{{ t('lyrics') }}</span>
      <textarea v-model="form.lyrics" class="lyrics" rows="12" required spellcheck="false" :placeholder="t('lyricsPlaceholder')" />
      <small class="muted">{{ t('lyricsHint') }}</small>
      <small v-if="fieldErrors.lyrics" class="danger">{{ fieldErrors.lyrics.join(' ') }}</small>
    </label>

    <label class="check">
      <input v-model="form.instrumental" type="checkbox" />
      <span>{{ t('instrumental') }}</span>
    </label>

    <div class="row">
      <fieldset class="segmented">
        <legend>{{ t('quality') }}</legend>
        <label :class="{ active: form.quality === 'draft' }">
          <input v-model="form.quality" type="radio" value="draft" class="sr-only" />{{ t('qualityDraft') }}
        </label>
        <label :class="{ active: form.quality === 'full' }">
          <input v-model="form.quality" type="radio" value="full" class="sr-only" />{{ t('qualityFull') }}
        </label>
      </fieldset>

      <label class="field compact">
        <span>{{ t('batch') }}</span>
        <select v-model.number="form.batch">
          <option v-for="n in 4" :key="n" :value="n">{{ n }}</option>
        </select>
      </label>
    </div>
    <small class="muted">{{ t('qualityHint') }}</small>

    <details class="advanced">
      <summary>{{ t('advanced') }}</summary>
      <div class="grid">
        <label class="field">
          <span>{{ t('cot') }}</span>
          <select v-model="form.cot">
            <option value="full">{{ t('cotFull') }}</option>
            <option value="melody">{{ t('cotMelody') }}</option>
            <option value="off">{{ t('cotOff') }}</option>
          </select>
        </label>
        <label class="field">
          <span>{{ t('seed') }}</span>
          <input v-model="form.seed" type="text" inputmode="numeric" pattern="[0-9]*" :placeholder="t('seedPlaceholder')" />
        </label>
        <label class="field">
          <span>{{ t('draftSteps') }}</span>
          <input v-model.number="form.draftSteps" type="number" min="1" max="32" :disabled="form.quality !== 'draft'" />
        </label>
        <label class="field">
          <span>{{ t('engines') }}</span>
          <select v-model="form.engines">
            <option value="">{{ t('enginesAuto') }}</option>
            <option value="gpu">{{ t('enginesGpu') }}</option>
            <option value="gpu+ane">{{ t('enginesAne') }}</option>
          </select>
        </label>
      </div>
    </details>

    <div class="actions">
      <button type="submit" class="button primary" :disabled="sending">{{ sending ? t('generating') : t('generate') }}</button>
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

.lyrics {
  font-family: var(--font-mono);
  font-size: 0.9rem;
}

.row {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 1rem;
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

.advanced summary {
  cursor: pointer;
  font-weight: 600;
}

.grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(13rem, 1fr));
  gap: 0.9rem;
  margin-top: 0.9rem;
}

.actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 1rem;
}
</style>
