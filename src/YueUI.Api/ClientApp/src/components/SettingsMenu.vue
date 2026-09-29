<script setup lang="ts">
import { computed, onMounted, ref, useTemplateRef, watch } from 'vue'
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import { disablePush, enablePush, initPush, pushState, updatePushLanguage } from '../push'
import { locale, setLocale, t } from '../i18n'
import { reload, standalone } from '../update'

/**
 * What is set once per device rather than used while working: notifications, the language and, in the home-screen app,
 * a reload. Behind one button, so the menu bar keeps its room for the pages.
 */
const emit = defineEmits<{ notice: [text: string, error: boolean] }>()

const menu = useTemplateRef<InstanceType<typeof Menu>>('menu')
const items = computed<MenuItem[]>(() => [
  {
    label: t(pushState.value === 'on' ? 'notificationsSwitchOff' : 'notificationsSwitchOn'),
    icon: pushState.value === 'on' ? 'pi pi-bell-slash' : 'pi pi-bell',
    disabled: working.value,
    command: () => void toggle(),
  },
  { label: t('language'), icon: 'pi pi-globe', command: () => setLocale(locale.value === 'de' ? 'en' : 'de') },
  ...(standalone ? [{ label: t('reload'), icon: 'pi pi-refresh', command: reload }] : []),
])

const working = ref(false)
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
    icon="pi pi-cog"
    severity="secondary"
    text
    rounded
    :loading="working"
    :aria-label="t('settings')"
    aria-haspopup="true"
    v-tooltip.bottom="t('settings')"
    @click="menu?.toggle($event)"
  />
  <Menu ref="menu" :model="items" popup />
</template>
