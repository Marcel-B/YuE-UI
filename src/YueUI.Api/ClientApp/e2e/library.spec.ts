import { expect, run, song, test } from './fakeApi'

test.beforeEach(({ api }) => {
  api.runs = [
    run('run-1', { title: 'Nachtzug', style: 'synthwave', lyrics: '[verse]\nLichter der Stadt' }),
    run('run-2', {
      title: 'Sommerregen',
      style: 'acoustic folk',
      lyrics: '[chorus]\nRegen auf dem Dach',
      songs: [song('run-2/song1', { rating: 3 }), song('run-2/song2')],
    }),
  ]
})

test('the search filters title and style, or the lyrics', async ({ page }) => {
  await page.goto('#/songs')
  await expect(page.getByText('Nachtzug')).toBeVisible()
  await expect(page.getByText('Sommerregen')).toBeVisible()

  const search = page.getByRole('searchbox', { name: 'Suchen' })
  await search.fill('folk')
  await expect(page.getByText('1 von 2 Einträgen')).toBeVisible()
  await expect(page.getByText('Nachtzug')).toBeHidden()

  // The lyrics are not searched with the title.
  await search.fill('stadt')
  await expect(page.getByText('Keine Treffer.')).toBeVisible()
})

test('a rating goes to the server', async ({ page, api }) => {
  await page.goto('#/songs')
  const first = page.getByRole('group').filter({ hasText: 'Nachtzug' })
  await first.getByRole('radio').nth(3).check({ force: true })

  await expect
    .poll(() => api.callsTo('PUT', '/api/songs/run-1/song1/rating').map((call) => call.body))
    .toEqual([{ rating: 4 }])
})
