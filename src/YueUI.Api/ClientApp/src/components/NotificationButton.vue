<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { disablePush, enablePush, initPush, pushState, updatePushLanguage } from '../push'
import { locale, t } from '../i18n'

const emit = defineEmits<{ notice: [text: string, error: boolean] }>()

const working = ref(false)
const label = computed(() => t(pushState.value === 'on' ? 'notificationsOn' : 'notificationsOff'))

onMounted(() => {
  initPush().catch((caught: unknown) => console.warn('Web Push unavailable', caught))
})
watch(locale, () => void updatePushLanguage().catch(() => undefined))

async function toggle(): Promise<void> {
  switch (pushState.value) {
    case 'unsupported':
      emit('notice', t('notificationsUnsupported'), true)
      return
    case 'install':
      emit('notice', t('notificationsInstall'), false)
      return
    case 'denied':
      emit('notice', t('notificationsBlocked'), true)
      return
  }
  working.value = true
  try {
    if (pushState.value === 'on') {
      await disablePush()
      emit('notice', t('notificationsDisabled'), false)
    } else {
      const state = await enablePush()
      if (state === 'on') {
        emit('notice', t('notificationsEnabled'), false)
      } else if (state === 'denied') {
        emit('notice', t('notificationsBlocked'), true)
      }
    }
  } catch (caught) {
    emit(
      'notice',
      t('notificationsFailed', { message: caught instanceof Error ? caught.message : String(caught) }),
      true,
    )
  } finally {
    working.value = false
  }
}
</script>

<template>
  <Button
    :icon="pushState === 'on' ? 'pi pi-bell' : 'pi pi-bell-slash'"
    :severity="pushState === 'on' ? undefined : 'secondary'"
    :loading="working"
    :aria-label="label"
    :aria-pressed="pushState === 'on'"
    v-tooltip.bottom="label"
    text
    rounded
    @click="toggle"
  />
</template>
