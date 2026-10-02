<script setup lang="ts">
import { computed, onMounted, ref, useTemplateRef, watch } from 'vue'
import Menu from 'primevue/menu'
import type { MenuItem } from 'primevue/menuitem'
import { disablePush, enablePush, initPush, pushState, updatePushLanguage } from '../push'
import { formatDateTime, locale, setLocale, t } from '../i18n'
import { reload, standalone } from '../update'
import { ApiError, getBackup, startBackup } from '../api'
import type { BackupStatus } from '../types'

/**
 * What is set once per device rather than used while working: notifications, the language, the Nextcloud backup (only
 * when the server has one configured) and, in the home-screen app, a reload. Behind one button, so the menu bar keeps
 * its room for the pages.
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
  ...backupItems.value,
])

const backup = ref<BackupStatus | null>(null)
const backupItems = computed<MenuItem[]>(() => {
  const status = backup.value
  if (!status?.configured) {
    return []
  }
  const last = status.last
  return [
    { separator: true },
    {
      label: status.running
        ? t('backupRunning', { done: status.done, total: status.total })
        : last === null
          ? t('backupNever')
          : t(last.success ? 'backupLast' : 'backupFailed', { time: formatDateTime(last.finishedAt) }),
      icon: status.running
        ? 'pi pi-spin pi-spinner'
        : last?.success === false
          ? 'pi pi-exclamation-triangle'
          : 'pi pi-cloud',
      // Only a failure has more to say: why, in the notice.
      disabled: last?.success !== false || status.running,
      command: () => emit('notice', last?.error ?? '', true),
    },
    { label: t('backupNow'), icon: 'pi pi-cloud-upload', disabled: status.running, command: () => void backUp() },
  ]
})

async function open(event: Event): Promise<void> {
  menu.value?.toggle(event)
  try {
    backup.value = await getBackup()
  } catch {
    // An older server without the backup, or no connection: the menu just leaves it out.
  }
}

async function backUp(): Promise<void> {
  try {
    backup.value = await startBackup()
    emit('notice', t('backupStarted'), false)
  } catch (caught) {
    emit(
      'notice',
      t('backupError', {
        message: caught instanceof ApiError || caught instanceof Error ? caught.message : String(caught),
      }),
      true,
    )
  }
}

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
    @click="void open($event)"
  />
  <Menu ref="menu" :model="items" popup />
</template>
