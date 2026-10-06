import { expect, queued, test } from './fakeApi'

test('a song is asked for with what the form holds', async ({ page, api }) => {
  await page.goto('')
  await page.getByRole('textbox', { name: 'Titel' }).fill('Nachtzug')
  await page.getByRole('textbox', { name: 'Stil' }).fill('synthwave, male vocals')
  await page.getByRole('textbox', { name: 'Songtext' }).fill('[verse]\nUnter der Stadt')
  await page.getByRole('button', { name: 'Erzeugen' }).click()

  await expect(page.getByRole('status').filter({ hasText: 'In der Warteschlange' })).toBeVisible()
  const [call] = api.callsTo('POST', '/api/generate')
  expect(call!.body).toMatchObject({
    title: 'Nachtzug',
    style: 'synthwave, male vocals',
    lyrics: '[verse]\nUnter der Stadt',
    quality: 'draft',
    batch: 1,
    seed: null,
    abc: null,
  })
})

test('a song that has to wait says so', async ({ page, api }) => {
  api.answers.set('POST /api/generate', () => queued('job1'))
  await page.goto('')
  await page.getByRole('textbox', { name: 'Stil' }).fill('jazz')
  await page.getByRole('textbox', { name: 'Songtext' }).fill('la la')
  await page.getByRole('button', { name: 'Erzeugen' }).click()

  await expect(page.getByRole('status').filter({ hasText: 'Wartet in der Warteschlange' })).toBeVisible()
})

test('the form outlasts a reload', async ({ page }) => {
  await page.goto('')
  await page.getByRole('textbox', { name: 'Stil' }).fill('bossa nova')
  // Kept on change; give the watcher its tick before reloading.
  await expect.poll(() => page.evaluate(() => localStorage.getItem('yue-ui.form') ?? '')).toContain('bossa nova')
  await page.reload()

  await expect(page.getByRole('textbox', { name: 'Stil' })).toHaveValue('bossa nova')
})

test('the queue page lists what waits and why', async ({ page, api }) => {
  api.snapshot.queue = [queued('job1', { title: 'Erster' }), queued('job2', { kind: 'lyrics', title: 'Herbst' })]
  api.snapshot.worker.busy = true
  await page.goto('#/queue')

  await expect(page.getByText('Erster')).toBeVisible()
  await expect(page.getByText('Herbst')).toBeVisible()

  await api.emit('queue', [])
  await expect(page.getByText('Erster')).toBeHidden()
})
