<script setup lang="ts">
import { t } from '../i18n'
import { knownSectionTags, otherSectionTags } from '../lyricsTags'
import FieldHelp from './FieldHelp.vue'

/** Inserting is the form's job, since it knows where the caret was in the lyrics field. */
const emit = defineEmits<{ pick: [tag: string] }>()
</script>

<template>
  <div class="mt-2">
    <FieldHelp id="gen-section-tags-help" :hint="t('sectionTagsIntro')" :more="t('sectionTagsMore')" />
    <div class="mt-2 flex flex-wrap gap-2" role="group" :aria-label="t('sectionTags')">
      <Button
        v-for="tag in knownSectionTags"
        :key="tag"
        type="button"
        size="small"
        rounded
        outlined
        :label="tag"
        @click="emit('pick', tag)"
      />
    </div>
    <small class="muted mt-3 block">{{ t('sectionTagsOther') }}</small>
    <div class="mt-2 flex flex-wrap gap-2" role="group" :aria-label="t('sectionTagsOther')">
      <Button
        v-for="tag in otherSectionTags"
        :key="tag"
        type="button"
        size="small"
        rounded
        outlined
        severity="secondary"
        :label="tag"
        @click="emit('pick', tag)"
      />
    </div>
  </div>
</template>
