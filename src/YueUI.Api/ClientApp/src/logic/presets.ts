import { listPresets, putPreset, removePreset, type StoredPreset } from './api'
import { defaultFormState, type FormState } from './options'

/**
 * Named parameter sets, so a song can be converted the same way again without clicking through every
 * option. They are kept on the server, like the instruments, so every browser the interface is opened from
 * offers the same ones; the server stores the form as it is and hands it back unread.
 */
export interface Preset {
  name: string
  form: FormState
}

/** The presets from the server. */
export async function loadPresets(): Promise<Preset[]> {
  return (await listPresets()).map(fromStored)
}

/** Adds the preset, or replaces the one of the same name; resolves with the list as it now stands. */
export async function savePreset(name: string, form: FormState): Promise<Preset[]> {
  await putPreset(name, { ...form })
  return (await listPresets()).map(fromStored)
}

export async function deletePreset(name: string): Promise<Preset[]> {
  await removePreset(name)
  return (await listPresets()).map(fromStored)
}

/** Settings saved before an option existed fall back to its default, as the last used ones do. */
function fromStored(stored: StoredPreset): Preset {
  const form = stored.form && typeof stored.form === 'object' ? (stored.form as Partial<FormState>) : {}
  return { name: stored.name, form: { ...defaultFormState(), ...form } }
}
