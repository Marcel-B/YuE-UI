import { ref } from 'vue'
import { createTemplate, deleteTemplate, getTemplates, updateTemplate } from './api'
import type { TemplatePart, TemplateSettings } from './form'
import { t, type MessageKey } from './i18n'
import type { PromptTemplate } from './types'

/**
 * The prompt templates, by name. The server keeps them (in the database, so the backup takes them along), and the
 * create page and the templates page share this copy.
 */
export const templates = ref<PromptTemplate[]>([])

/**
 * The template the form was last filled from, so that saving offers to overwrite it. Only in memory: after a reload
 * the form may have moved far from it.
 */
export const loadedTemplateId = ref<number | null>(null)

let loading: Promise<void> | null = null

/** Asked once per page load; every change below keeps the copy current. */
export function loadTemplates(force = false): Promise<void> {
  if (!loading || force) {
    loading = getTemplates()
      .then((list) => {
        templates.value = list
      })
      .catch((caught: unknown) => {
        loading = null
        throw caught
      })
  }
  return loading
}

function byName(list: PromptTemplate[]): PromptTemplate[] {
  return [...list].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }) || a.id - b.id)
}

function put(template: PromptTemplate): void {
  templates.value = byName([...templates.value.filter((t) => t.id !== template.id), template])
}

/** The template of that name, in any case, as the server compares them. */
export function templateNamed(name: string): PromptTemplate | undefined {
  const key = name.trim().toLocaleUpperCase()
  return templates.value.find((t) => t.name.toLocaleUpperCase() === key)
}

/** Saves under a new name, or overwrites the template that has it. */
export async function saveTemplate(name: string, settings: TemplateSettings): Promise<PromptTemplate> {
  const existing = templateNamed(name)
  const saved = existing
    ? await updateTemplate(existing.id, { name: name.trim(), settings })
    : await createTemplate(name.trim(), settings)
  put(saved)
  loadedTemplateId.value = saved.id
  return saved
}

export async function renameTemplate(id: number, name: string): Promise<PromptTemplate> {
  const renamed = await updateTemplate(id, { name: name.trim() })
  put(renamed)
  return renamed
}

export async function removeTemplate(id: number): Promise<void> {
  await deleteTemplate(id)
  templates.value = templates.value.filter((t) => t.id !== id)
  if (loadedTemplateId.value === id) {
    loadedTemplateId.value = null
  }
}

export const partLabels: Record<TemplatePart, MessageKey> = {
  style: 'templatePartStyle',
  seed: 'templatePartSeed',
  parameters: 'templatePartParameters',
  voice: 'templatePartVoice',
}

export function partList(parts: TemplatePart[]): string {
  return parts.map((part) => t(partLabels[part])).join(', ')
}
