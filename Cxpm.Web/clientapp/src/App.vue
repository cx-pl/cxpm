<script setup lang="ts">
import { RouterLink, RouterView } from 'vue-router'
</script>

<template>
  <div class="app-shell">
    <header class="topbar">
      <RouterLink class="brand" to="/" aria-label="cxpm home">
        <span class="brand-mark">c</span>
        <span class="brand-name">cxpm</span>
        <span class="brand-context">PACKAGE REPOSITORY</span>
      </RouterLink>
      <nav aria-label="Main navigation">
        <RouterLink to="/">Overview</RouterLink>
        <RouterLink to="/about">About</RouterLink>
      </nav>
      <span class="version-chip">EARLY ACCESS</span>
    </header>

    <RouterView />

    <footer class="site-footer">
      <span>Built for the CX ecosystem</span>
      <span>API base <code>/api</code></span>
    </footer>
  </div>
</template>
