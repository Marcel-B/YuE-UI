import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'
import { brotliCompressSync, constants, gzipSync } from 'node:zlib'
import { defineConfig, type Plugin } from 'vite'

// The API (src/YueUI.Api) serves this app under /ui. During `dotnet run`, SpaProxy starts this dev server and
// sends the browser here; /api requests (including the event stream) are forwarded back to the API. That is the
// development API on 5091, not the installed app on 5090, so trying out the UI never touches the real queue.
const apiTarget = process.env.YUE_API_URL ?? 'http://127.0.0.1:5091'

export default defineConfig({
  base: '/ui/',
  plugins: [vue(), tailwindcss(), presetCoversStyles(), buildVersion(), precompress()],
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

/**
 * Gives every build an id, compiled into the app (`__BUILD_ID__`) and written to `version.json` next to it. The home
 * screen app on iOS stays in memory and resumes without reloading, so after a deploy it keeps running the old build;
 * `src/update.ts` compares the two and offers a reload. During development the id is `dev` and nothing is checked.
 */
function buildVersion(): Plugin {
  let id = 'dev'
  return {
    name: 'build-version',
    config(_, { command }) {
      if (command === 'build') id = Date.now().toString(36)
      return { define: { __BUILD_ID__: JSON.stringify(id) } }
    },
    generateBundle() {
      this.emitFile({ type: 'asset', fileName: 'version.json', source: JSON.stringify({ build: id }) })
    },
  }
}

/**
 * Writes a Brotli (`.br`) and a gzip (`.gz`) copy next to every script and style sheet. Neither Kestrel nor
 * `tailscale serve` compresses, so the phone would otherwise download the ~800 kB of the first load as they are;
 * the server picks the copy the browser accepts (ClientAppAssets.cs). Packing once here, at the highest levels,
 * costs the Mac nothing per request.
 */
function precompress(): Plugin {
  return {
    name: 'precompress',
    apply: 'build',
    writeBundle(options, bundle) {
      const dir = options.dir ?? 'dist'
      for (const fileName of Object.keys(bundle)) {
        if (!/\.(js|css|svg)$/.test(fileName)) continue
        const path = join(dir, fileName)
        const source = readFileSync(path)
        // Tiny files gain nothing that outweighs a second request header.
        if (source.length < 1024) continue
        const brotli = brotliCompressSync(source, {
          params: {
            [constants.BROTLI_PARAM_QUALITY]: constants.BROTLI_MAX_QUALITY,
            [constants.BROTLI_PARAM_SIZE_HINT]: source.length,
          },
        })
        const gzip = gzipSync(source, { level: 9 })
        if (brotli.length < source.length) writeFileSync(`${path}.br`, brotli)
        if (gzip.length < source.length) writeFileSync(`${path}.gz`, gzip)
      }
    },
  }
}
