<script setup lang="ts">
/**
 * One module of a synthesizer (an oscillator, the filter, an envelope) as a framed panel with its name in the
 * accent colour, as on the Logic plugin: the switches of the module sit in its header, its knobs wrap in rows below.
 */
defineProps<{ title: string }>()
</script>

<template>
  <section class="module">
    <header>
      <h3>{{ title }}</h3>
      <slot name="header" />
    </header>
    <slot name="before" />
    <div class="knobs">
      <slot />
    </div>
    <slot name="after" />
  </section>
</template>

<style scoped>
.module {
  min-width: 0;
  padding: 0.5rem 0.625rem 0.625rem;
  border: 1px solid var(--border);
  border-radius: var(--radius-small);
  background: var(--surface-sunken);
}

header {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 0.25rem 0.5rem;
  margin-bottom: 0.375rem;
}

h3 {
  margin: 0;
  color: var(--accent);
  font-size: 0.8rem;
  font-weight: 600;
  letter-spacing: 0.02em;
}

.knobs {
  display: flex;
  flex-wrap: wrap;
  gap: 0.375rem 0.25rem;
}
</style>
