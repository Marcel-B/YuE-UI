import { defineConfig } from 'vitest/config'

// The unit tests cover plain TypeScript (no components), so they need none of the app's Vite plugins; e2e/ holds
// the Playwright tests, which run in a browser.
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
  },
})
