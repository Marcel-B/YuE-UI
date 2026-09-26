import Base from '@primevue/core/base'

/**
 * Keeps PrimeVue from writing its theme styles again every time one of its components updates.
 *
 * PrimeVue 4 (up to 4.5.5) calls `Base.clearLoadedStyleNames` whenever a directive updates and whenever a component
 * mounts, to pick up a theme changed at runtime. Every Button carries the Ripple directive, and a component with a
 * directive re-renders with its parent, so each re-render made the next component write its base `<style>` again,
 * and the browser recalculated the styles of the whole page. With the library open and a song generating, that was
 * over a thousand style writes a second from the progress events alone; Safari reloaded the page for using too much
 * energy. This app never changes the theme at runtime (dark mode follows the system through CSS), so the styles
 * loaded once stay valid. Remove this when PrimeVue no longer clears the list on every update.
 */
Base.clearLoadedStyleNames = () => {}
