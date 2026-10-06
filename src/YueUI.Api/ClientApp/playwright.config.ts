import { defineConfig, devices } from '@playwright/test'

// The browser tests run the Vite dev server and answer /api in the page itself (e2e/fakeApi.ts), so they need
// neither the .NET server nor YuE Studio. PLAYWRIGHT_CHROMIUM points at a Chromium of another Playwright version
// where one is installed already (the cloud container); CI installs the matching one.
const port = 5199

export default defineConfig({
  testDir: 'e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  use: {
    baseURL: `http://127.0.0.1:${port}/ui/`,
    locale: 'de-DE',
    trace: 'retain-on-failure',
    // Requests from a service worker would pass the page's routes.
    serviceWorkers: 'block',
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM } : {},
  },
  // The phone is the main client.
  projects: [{ name: 'phone', use: { ...devices['Pixel 7'] } }],
  webServer: {
    command: `npx vite --port ${port} --strictPort`,
    url: `http://127.0.0.1:${port}/ui/`,
    reuseExistingServer: !process.env.CI,
  },
})
