<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Checkbox from 'primevue/checkbox'
import { ApiError } from '../api'
import { applyTemplate, partsOf, templateParts, toTemplateSettings, type FormState, type TemplatePart } from '../form'
import { t } from '../i18n'
import {
  loadedTemplateId,
  loadTemplates,
  partLabels,
  partList,
  saveTemplate,
  templateNamed,
  templates,
} from '../templates'
import { navigate } from '../view'

/**
 * Prompt templates on the create page: one picker that fills the form from a template, and a short panel that saves
 * the form as one (or overwrites the template it came from). The templates page renames and deletes them.
 */
const props = defineProps<{
  /** Songs can be sung with another voice; without it the voice is no part worth saving. */
  voices: boolean
}>()
const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()
const form = defineModel<FormState>({ required: true })

onMounted(() => {
  loadTemplates().catch(() => undefined)
})

/** The picker shows the template the form came from until another is chosen or the form is cleared. */
const chosen = computed({
  get: () => loadedTemplateId.value,
  set: (id: number | null) => {
    const template = templates.value.find((tpl) => tpl.id === id)
    if (!template) {
      return
    }
    form.value = applyTemplate(form.value, template.settings)
    loadedTemplateId.value = template.id
    emit('notice', t('templateApplied', { name: template.name, parts: partList(partsOf(template.settings)) }))
  },
})

const saving = ref(false)
const panelOpen = ref(false)
const name = ref('')
const parts = ref<TemplatePart[]>([])
const offeredParts = computed(() => templateParts.filter((part) => part !== 'voice' || props.voices))

function openPanel(): void {
  if (panelOpen.value) {
    panelOpen.value = false
    return
  }
  const loaded = templates.value.find((tpl) => tpl.id === loadedTemplateId.value)
  name.value = loaded?.name ?? ''
  // The loaded template's groups, so overwriting it keeps its shape; otherwise what the form has set.
  parts.value = loaded
    ? partsOf(loaded.settings).filter((part) => offeredParts.value.includes(part))
    : offeredParts.value.filter(
        (part) =>
          part === 'style' ||
          part === 'parameters' ||
          (part === 'seed' && form.value.seed.trim() !== '') ||
          (part === 'voice' && form.value.voiceId !== ''),
      )
  panelOpen.value = true
}

const existing = computed(() => (name.value.trim() === '' ? undefined : templateNamed(name.value)))
const canSave = computed(() => name.value.trim() !== '' && parts.value.length > 0 && !saving.value)

async function save(): Promise<void> {
  if (!canSave.value) {
    return
  }
  saving.value = true
  try {
    const overwrote = existing.value !== undefined
    const saved = await saveTemplate(name.value, toTemplateSettings(form.value, parts.value))
    panelOpen.value = false
    emit('notice', t(overwrote ? 'templateOverwritten' : 'templateSaved', { name: saved.name }))
  } catch (caught) {
    emit(
      'error',
      caught instanceof ApiError && caught.status === 0
        ? t('errorNetwork')
        : t('errorGeneric', { message: caught instanceof Error ? caught.message : String(caught) }),
    )
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <div>
    <div class="flex items-center gap-1">
      <Select
        v-model="chosen"
        :options="templates"
        option-label="name"
        option-value="id"
        :placeholder="templates.length > 0 ? t('templateLoad') : t('templatesNone')"
        :disabled="templates.length === 0"
        :filter="templates.length > 8"
        :aria-label="t('templateLoad')"
        class="min-w-0 flex-1"
      />
      <Button
        type="button"
        icon="pi pi-bookmark"
        text
        :aria-label="t('templateSaveAs')"
        :aria-expanded="panelOpen"
        aria-controls="template-save"
        v-tooltip.bottom="t('templateSaveAs')"
        @click="openPanel"
      />
      <Button
        type="button"
        icon="pi pi-list"
        text
        severity="secondary"
        :aria-label="t('templatesManage')"
        v-tooltip.bottom="t('templatesManage')"
        @click="navigate('templates')"
      />
    </div>

    <form v-if="panelOpen" id="template-save" class="template-save mt-2 flex flex-col gap-2" @submit.prevent="save">
      <InputText
        v-model="name"
        fluid
        maxlength="100"
        enterkeyhint="done"
        :placeholder="t('templateName')"
        :aria-label="t('templateName')"
      />
      <div class="flex flex-wrap gap-x-4 gap-y-1">
        <div v-for="part in offeredParts" :key="part" class="flex items-center gap-2">
          <Checkbox v-model="parts" :input-id="`template-part-${part}`" :value="part" />
          <label :for="`template-part-${part}`" class="text-sm">
            {{ t(partLabels[part]) }}
            <span v-if="part === 'seed' && form.seed.trim() === ''" class="muted">({{ t('templateSeedRandom') }})</span>
          </label>
        </div>
      </div>
      <small v-if="existing" class="muted">{{ t('templateOverwriteHint', { name: existing.name }) }}</small>
      <div class="flex flex-wrap items-center gap-2">
        <Button
          type="submit"
          size="small"
          :icon="existing ? 'pi pi-replay' : 'pi pi-check'"
          :label="existing ? t('templateOverwrite') : t('save')"
          :severity="existing ? 'warn' : undefined"
          :loading="saving"
          :disabled="!canSave"
        />
        <Button type="button" size="small" text severity="secondary" :label="t('cancel')" @click="panelOpen = false" />
      </div>
    </form>
  </div>
</template>

<style scoped>
.template-save {
  padding: 0.75rem;
  border-radius: var(--p-border-radius-md);
  background: var(--surface-sunken);
}
</style>
