import { formatNumber, t } from './i18n'

/** How the synthesizers' sliders show their values. */

export const percent = (value: number) => `${Math.round(value * 100)} %`
export const signedPercent = (value: number) => `${value > 0.005 ? '+' : ''}${Math.round(value * 100)} %`
export const seconds = (value: number) => (value < 1 ? `${Math.round(value * 1000)} ms` : `${formatNumber(value, 2)} s`)
export const hertz = (value: number) =>
  value < 1000 ? `${Math.round(value)} Hz` : `${formatNumber(value / 1000, 1)} kHz`
export const signed = (value: number, unit: string, digits = 0) =>
  `${value > 0 ? '+' : ''}${formatNumber(value, digits)}${unit}`
export const octaves = (value: number) => signed(value, ` ${t('synthOctaveUnit')}`, 1)
