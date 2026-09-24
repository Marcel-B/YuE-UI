import tailwindcss from '@tailwindcss/vite'
import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vite'

// The API (src/YueUI.Api) serves this app under /ui. During `dotnet run`, SpaProxy starts this dev server and
// sends the browser here; /api requests (including the event stream) are forwarded back to the API. That is the
// development API on 5091, not the installed app on 5090, so trying out the UI never touches the real queue.
const apiTarget = process.env.YUE_API_URL ?? 'http://127.0.0.1:5091'

export default defineConfig({
  base: '/ui/',
  plugins: [vue(), tailwindcss()],
  server: {
    host: '127.0.0.1',
    // 5173 belongs to YuE to Logic's dev server, so both can run side by side.
    port: 5174,
    // SpaProxy expects the dev server at exactly this address (SpaProxyServerUrl in the .csproj).
    strictPort: true,
    proxy: { '/api': apiTarget },
  },
})
