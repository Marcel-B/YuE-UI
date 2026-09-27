import { t } from './i18n'

/** Whole octaves only: any other shift would sing out of key over the song's own accompaniment. */
export function octaveOptions(): { value: number; label: string }[] {
  return [
    { value: -12, label: t('octaveDown') },
    { value: 0, label: t('octaveNone') },
    { value: 12, label: t('octaveUp') },
  ]
}

export function strengthOptions(): { value: number; label: string }[] {
  return [
    { value: 0.5, label: t('strengthLight') },
    { value: 0.7, label: t('strengthMedium') },
    { value: 0.9, label: t('strengthStrong') },
  ]
}

export function stepOptions(): { value: number; label: string }[] {
  return [
    { value: 25, label: t('stepsFast') },
    { value: 50, label: t('stepsNormal') },
    { value: 100, label: t('stepsFine') },
  ]
}
