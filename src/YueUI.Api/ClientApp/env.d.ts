/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the YueUI API; empty when the frontend is served by the API itself. */
  readonly VITE_API_BASE?: string
}
