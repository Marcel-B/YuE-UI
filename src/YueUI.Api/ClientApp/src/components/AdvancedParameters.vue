<script setup lang="ts">
import { computed } from 'vue'
import { Panel } from 'primevue'
import {
  advancedChanged,
  defaultSampling,
  exampleScore,
  lengthChoices,
  maxSongSeconds,
  resetAdvanced,
  type FormState,
  type SamplingPhase,
} from '../form'
import { formatDuration, t } from '../i18n'
import FieldHelp from './FieldHelp.vue'
import NumberField from './NumberField.vue'
import SamplingFields from './SamplingFields.vue'

/**
 * The parameters beside the song's own fields, in a column of their own on wide screens. They edit the same form as
 * GenerateForm and show the field errors of its last submission.
 */
defineProps<{
  /** Whether the worker takes the extension's fields; null while unknown (no worker has started yet). */
  extensions: boolean | null
}>()
const form = defineModel<FormState>({ required: true })
const fieldErrors = defineModel<Record<string, string[]>>('errors', { required: true })

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
  { value: 'off', label: t('cotOff') },
]
const engineOptions = [
  { value: '', label: t('defaultValue', { value: t('enginesAuto') }) },
  { value: 'gpu', label: t('enginesGpu') },
  { value: 'gpu+ane', label: t('enginesAne') },
]
const lengthOptions = lengthChoices.map((x) => ({ value: x, label: lengthLabel(x) }))
</script>

<template>
  <Panel toggleable>
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
          <NumberField
            id="gen-steps"
            v-model="steps"
            :min="1"
            :max="form.quality === 'draft' ? 32 : 64"
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
</template>
