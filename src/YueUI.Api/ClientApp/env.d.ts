/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the YueUI API; empty when the frontend is served by the API itself. */
  readonly VITE_API_BASE?: string
}

// PrimeVue's style bookkeeping, which it ships without types (patched in src/primevueStyles.ts).
declare module '@primevue/core/base' {
  const Base: { clearLoadedStyleNames(): void }
  export default Base
}
