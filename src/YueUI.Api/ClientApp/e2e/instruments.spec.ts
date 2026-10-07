import { expect, test } from './fakeApi'

test.beforeEach(async ({ page, api }) => {
  api.answers.set('GET /api/instruments', () => [])
  api.answers.set('GET /api/logic/synths/presets', () => [])
  // A browser whose MIDI permission is granted and whose next access finds one device more, as Firefox does for a
  // synthesizer switched on after the page first asked.
  await page.addInitScript(() => {
    const found = [['Mother-32'], ['Mother-32', 'M-VAVE FM-1']]
    let requests = 0
    Object.defineProperty(navigator, 'requestMIDIAccess', {
      value: () => {
        const names = found[Math.min(requests++, found.length - 1)]!
        const access = new EventTarget() as EventTarget & {
          outputs: Map<string, unknown>
          inputs: Map<string, unknown>
        }
        access.outputs = new Map(names.map((name) => [name, { id: name, name, send() {} }]))
        access.inputs = new Map()
        return Promise.resolve(access)
      },
    })
    const query = navigator.permissions.query.bind(navigator.permissions)
    navigator.permissions.query = (descriptor) =>
      descriptor.name === ('midi' as PermissionName)
        ? Promise.resolve({ state: 'granted' } as PermissionStatus)
        : query(descriptor)
  })
})

test('searching again finds a MIDI device switched on later', async ({ page }) => {
  await page.goto('#/instruments')

  const port = page.getByRole('combobox', { name: 'MIDI-Ausgang' })
  await port.click()
  await expect(page.getByRole('option')).toHaveText(['Mother-32', /^Anderer Ausgang/])
  await page.keyboard.press('Escape')

  await page.getByRole('button', { name: 'MIDI-Geräte neu suchen' }).click()
  await port.click()
  await expect(page.getByRole('option')).toHaveText(['Mother-32', 'M-VAVE FM-1', /^Anderer Ausgang/])
})

test('an instrument keeps a note written under the speech bubble', async ({ page, api }) => {
  api.answers.set('GET /api/instruments', () => [
    { id: 7, name: 'Mother-32', port: 'Mother-32', channel: 1, kind: 'Synth', drums: null, note: null },
  ])
  await page.goto('#/instruments')

  await page.getByRole('button', { name: 'Notiz', exact: true }).click()
  await page.getByRole('textbox', { name: 'Notiz' }).fill('Audio an Eingang 3')
  await page.getByRole('textbox', { name: 'Notiz' }).blur()

  await expect
    .poll(() => api.callsTo('PUT', '/api/instruments/7/note').map((call) => call.body))
    .toEqual([{ note: 'Audio an Eingang 3' }])
  // The button fills once a note is written, so it shows with the note closed.
  await expect(page.getByRole('button', { name: 'Notiz (vorhanden)' })).toBeVisible()
})
