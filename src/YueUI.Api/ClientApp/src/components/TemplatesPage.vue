<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'
import { useConfirm } from 'primevue/useconfirm'
import { ApiError } from '../api'
import { defaultSampling, maxSongSeconds, partsOf, type Sampling } from '../form'
import { formatDateTime, formatDuration, t } from '../i18n'
import { loadTemplates, partList, removeTemplate, renameTemplate, templates } from '../templates'
import type { Cot, PromptTemplate, Quality } from '../types'

/** The prompt templates as a list: what each holds, and loading, renaming and deleting them. */
const props = defineProps<{ active: boolean }>()
const emit = defineEmits<{
  apply: [template: PromptTemplate]
  notice: [message: string]
  error: [message: string]
}>()

const loadError = ref<string | null>(null)

// Asked again each time the page is shown: another browser may have saved one meanwhile.
watch(
  () => props.active,
  (active) => {
    if (active) {
      loadTemplates(true)
        .then(() => (loadError.value = null))
        .catch((caught: unknown) => (loadError.value = errorText(caught)))
    }
  },
  { immediate: true },
)

function errorText(caught: unknown): string {
  return caught instanceof ApiError && caught.status === 0
    ? t('errorNetwork')
    : t('errorGeneric', { message: caught instanceof Error ? caught.message : String(caught) })
}

function sampling(value: unknown): Partial<Sampling> {
  return value !== null && typeof value === 'object' ? (value as Partial<Sampling>) : {}
}

/** The values worth reading at a glance: the seed, and parameters that differ from the model's. */
function details(template: PromptTemplate): string[] {
  const s = template.settings
  const lines: string[] = []
  if (typeof s.seed === 'string') {
    lines.push(s.seed.trim() === '' ? `${t('seed')} ${t('templateSeedRandom')}` : `${t('seed')} ${s.seed}`)
  }
  if (s.instrumental === true) {
    lines.push(t('instrumental'))
  }
  if (typeof s.quality === 'string') {
    lines.push(`${t('quality')} ${t((s.quality as Quality) === 'full' ? 'qualityFull' : 'qualityDraft')}`)
  }
  if (typeof s.batch === 'number' && s.batch > 1) {
    lines.push(t('templateBatch', { n: s.batch }))
  }
  if (typeof s.cot === 'string' && s.cot !== 'full') {
    lines.push(`${t('cot')} ${t((s.cot as Cot) === 'melody' ? 'cotMelody' : 'cotOff')}`)
  }
  if (typeof s.maxSeconds === 'number' && s.maxSeconds !== maxSongSeconds) {
    lines.push(`${t('maxLength')} ${formatDuration(s.maxSeconds)}`)
  }
  const song = sampling(s.semanticSampling)
  const score = sampling(s.abcSampling)
  if (song.temperature !== undefined && song.temperature !== defaultSampling.semanticSampling.temperature) {
    lines.push(t('templateTemperatureSong', { value: song.temperature }))
  }
  if (score.temperature !== undefined && score.temperature !== defaultSampling.abcSampling.temperature) {
    lines.push(t('templateTemperatureScore', { value: score.temperature }))
  }
  const otherSampling = (['topP', 'topK', 'repetitionPenalty', 'penaltyWindow'] as const).some(
    (key) =>
      (song[key] !== undefined && song[key] !== defaultSampling.semanticSampling[key]) ||
      (score[key] !== undefined && score[key] !== defaultSampling.abcSampling[key]),
  )
  if (otherSampling) {
    lines.push(t('templateSamplingChanged'))
  }
  return lines
}

const editing = ref<number | null>(null)
const name = ref('')
const savingName = ref(false)
const nameInput = ref<{ $el: HTMLInputElement }[] | null>(null)

async function startRename(template: PromptTemplate): Promise<void> {
  editing.value = template.id
  name.value = template.name
  await nextTick()
  nameInput.value?.[0]?.$el.focus()
}

async function saveName(template: PromptTemplate): Promise<void> {
  if (name.value.trim() === '' || savingName.value) {
    return
  }
  savingName.value = true
  try {
    await renameTemplate(template.id, name.value)
    editing.value = null
  } catch (caught) {
    emit('error', errorText(caught))
  } finally {
    savingName.value = false
  }
}

const confirm = useConfirm()

function askDelete(template: PromptTemplate): void {
  confirm.require({
    header: t('confirmDelete'),
    message: t('confirmDeleteTemplate', { name: template.name }),
    icon: 'pi pi-trash',
    rejectProps: { label: t('keep'), severity: 'secondary', outlined: true },
    acceptProps: { label: t('delete'), severity: 'danger' },
    accept: async () => {
      try {
        await removeTemplate(template.id)
        emit('notice', t('templateDeleted', { name: template.name }))
      } catch (caught) {
        emit('error', errorText(caught))
      }
    },
  })
}
</script>

<template>
  <section>
    <p v-if="loadError" class="danger">{{ loadError }}</p>
    <p v-else-if="templates.length === 0" class="muted">{{ t('templatesEmpty') }}</p>
    <ul class="m-0 flex list-none flex-col gap-3 p-0">
      <li v-for="template in templates" :key="template.id" class="template">
        <form v-if="editing === template.id" class="flex items-center gap-1" @submit.prevent="saveName(template)">
          <InputText
            ref="nameInput"
            v-model="name"
            maxlength="100"
            :aria-label="t('templateName')"
            class="min-w-0 flex-1"
            @keydown.esc="editing = null"
          />
          <Button
            type="submit"
            icon="pi pi-check"
            text
            rounded
            :loading="savingName"
            :disabled="!name.trim()"
            :aria-label="t('save')"
          />
          <Button
            type="button"
            icon="pi pi-times"
            text
            rounded
            severity="secondary"
            :aria-label="t('cancel')"
            @click="editing = null"
          />
        </form>
        <div v-else class="flex items-center gap-1">
          <strong class="min-w-0 flex-1 truncate">{{ template.name }}</strong>
          <Button
            icon="pi pi-pencil"
            text
            rounded
            size="small"
            severity="secondary"
            :aria-label="t('rename')"
            v-tooltip.top="t('rename')"
            @click="startRename(template)"
          />
          <Button
            icon="pi pi-trash"
            text
            rounded
            size="small"
            severity="danger"
            :aria-label="t('delete')"
            v-tooltip.top="t('delete')"
            @click="askDelete(template)"
          />
          <Button icon="pi pi-sparkles" size="small" :label="t('templateUse')" @click="emit('apply', template)" />
        </div>
        <p v-if="typeof template.settings.style === 'string' && template.settings.style" class="style">
          {{ template.settings.style }}
        </p>
        <div v-if="details(template).length > 0" class="mt-1 flex flex-wrap gap-1">
          <Tag v-for="detail in details(template)" :key="detail" :value="detail" severity="secondary" />
        </div>
        <small class="muted mt-1 block">
          {{ partList(partsOf(template.settings)) }} · {{ formatDateTime(template.updatedAt) }}
        </small>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.template {
  padding: 0.75rem;
  border: 1px solid var(--p-content-border-color);
  border-radius: var(--p-border-radius-md);
}

.style {
  margin: 0.25rem 0 0;
  overflow: hidden;
  display: -webkit-box;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 3;
  line-clamp: 3;
  white-space: pre-line;
}
</style>
