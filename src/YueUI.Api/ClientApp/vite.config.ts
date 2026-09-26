import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig, type Plugin } from 'vite'

// The API (src/YueUI.Api) serves this app under /ui. During `dotnet run`, SpaProxy starts this dev server and
// sends the browser here; /api requests (including the event stream) are forwarded back to the API. That is the
// development API on 5091, not the installed app on 5090, so trying out the UI never touches the real queue.
const apiTarget = process.env.YUE_API_URL ?? 'http://127.0.0.1:5091'

export default defineConfig({
  base: '/ui/',
  plugins: [vue(), tailwindcss(), presetCoversStyles()],
  build: {
    rolldownOptions: {
      output: {
        // Vue and PrimeVue (with its Aura preset) make up most of the bundle and change only with a dependency
        // update. As separate chunks they stay cached across app deploys, and no chunk exceeds Vite's 500 kB warning.
        codeSplitting: {
          groups: [
            { name: 'vue', test: /[\\/]node_modules[\\/]@?vue[\\/]/ },
            // entriesAware keeps what only the lazily loaded library uses (DataView, Paginator, …) out of the chunk
            // the first page needs; one group would pull it in.
            { name: 'primevue', test: /[\\/]node_modules[\\/](primevue|@primevue)[\\/]/, entriesAware: true },
            // The Aura preset's design tokens, trimmed to the components in use (src/theme.ts).
            { name: 'primeuix', test: /[\\/]node_modules[\\/]@primeuix[\\/]/ },
          ],
        },
      },
    },
  },
  server: {
    host: '127.0.0.1',
    // 5173 belongs to YuE to Logic's dev server, so both can run side by side.
    port: 5174,
    // SpaProxy expects the dev server at exactly this address (SpaProxyServerUrl in the .csproj).
    strictPort: true,
    proxy: { '/api': apiTarget },
  },
})

/**
 * src/theme.ts lists the design tokens of the components in use rather than importing all of Aura. A component
 * whose tokens are missing renders without them and no error says so, so the build fails instead: every component
 * style that ends up in the bundle (`@primeuix/styles/<name>`) needs its tokens (`@primeuix/themes/aura/<name>`).
 */
function presetCoversStyles(): Plugin {
  const name = (id: string, pattern: RegExp) => id.replaceAll('\\', '/').match(pattern)?.[1]
  return {
    name: 'preset-covers-styles',
    apply: 'build',
    generateBundle(_, bundle) {
      const styles = new Set<string>()
      const tokens = new Set<string>()
      // What tree-shaking kept; the module graph also holds every style @primeuix/styles re-exports.
      const kept = Object.values(bundle).flatMap((output) =>
        output.type === 'chunk'
          ? Object.entries(output.modules)
              .filter(([, module]) => module.renderedLength > 0)
              .map(([id]) => id)
          : [],
      )
      for (const id of kept) {
        const style = name(id, /@primeuix\/styles\/dist\/([^/]+)\//)
        const token = name(id, /@primeuix\/themes\/dist\/aura\/([^/]+)\//)
        if (style && style !== 'base') styles.add(style)
        if (token) tokens.add(token)
      }
      const missing = [...styles].filter((style) => !tokens.has(style))
      if (missing.length > 0) {
        this.error(`src/theme.ts lacks the Aura tokens of: ${missing.join(', ')}`)
      }
    },
  }
}
