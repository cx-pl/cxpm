<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { RouterLink } from 'vue-router'

type ApiHealth = {
  status: string
  service: string
}

type PackageSummary = {
  id: string
  versions: string[]
}

const apiState = ref<'checking' | 'online' | 'offline'>('checking')
const apiMessage = ref('Connecting to the repository API…')
const packages = ref<PackageSummary[]>([])
const catalogMessage = ref('Loading packages…')

onMounted(async () => {
  try {
    const response = await fetch('/api/health')
    if (!response.ok) throw new Error(`HTTP ${response.status}`)
    const health = (await response.json()) as ApiHealth
    apiState.value = health.status === 'ok' ? 'online' : 'offline'
    apiMessage.value = health.status === 'ok' ? 'Repository API is responding' : 'Repository API needs attention'
  } catch {
    apiState.value = 'offline'
    apiMessage.value = 'Could not reach the repository API'
  }

  try {
    const response = await fetch('/api/packages')
    if (!response.ok) throw new Error(`HTTP ${response.status}`)
    packages.value = (await response.json()) as PackageSummary[]
    catalogMessage.value = packages.value.length === 0 ? 'No packages have been published yet.' : ''
  } catch {
    catalogMessage.value = 'The package catalog is unavailable right now.'
  }
})
</script>

<template>
  <main class="page-content">
    <section class="hero">
      <div class="hero-copy">
        <p class="eyebrow"><span class="eyebrow-line"></span> THE CX PACKAGE REPOSITORY</p>
        <h1>Packages that keep<br /><span>your CX projects moving.</span></h1>
        <p class="hero-description">
          A home for CX packages, versions, and dependencies. Browse the catalog and
          share reusable building blocks across the CX ecosystem.
        </p>
        <div class="hero-actions">
          <a class="button button-primary" href="#repository-status">Explore repository <span aria-hidden="true">↗</span></a>
          <a class="button button-secondary" href="https://github.com/cx-pl">About CX <span aria-hidden="true">→</span></a>
        </div>
      </div>

      <aside class="status-card" id="repository-status" aria-labelledby="status-title">
        <div class="card-topline">
          <span class="card-icon" aria-hidden="true">⌘</span>
          <span class="status-label">SERVICE STATUS</span>
          <span class="status-dot" :class="`status-${apiState}`"></span>
        </div>
        <h2 id="status-title">Repository API</h2>
        <p class="status-message" aria-live="polite">{{ apiMessage }}</p>
        <div class="card-divider"></div>
        <div class="status-detail"><span>Base URL</span><code>/api</code></div>
        <div class="status-detail"><span>Health check</span><code>GET /api/health</code></div>
      </aside>
    </section>

    <section class="foundation" aria-label="Repository capabilities">
      <div class="foundation-heading">
        <span class="section-index">01 / FOUNDATION</span>
        <h2>One place for every dependency.</h2>
      </div>
      <div class="foundation-grid">
        <article class="foundation-item">
          <span class="item-number">01</span>
          <h3>Package catalog</h3>
          <p>Discover CX libraries and inspect their published versions.</p>
        </article>
        <article class="foundation-item">
          <span class="item-number">02</span>
          <h3>Version history</h3>
          <p>Keep releases immutable and resolve compatible dependencies.</p>
        </article>
        <article class="foundation-item">
          <span class="item-number">03</span>
          <h3>Built for cxpm</h3>
          <p>Restore packages directly into your project from the command line.</p>
        </article>
      </div>
    </section>

    <section class="catalog" aria-labelledby="catalog-title">
      <div class="foundation-heading">
        <span class="section-index">02 / CATALOG</span>
        <h2 id="catalog-title">Available packages</h2>
      </div>
      <p v-if="catalogMessage" class="catalog-message" aria-live="polite">{{ catalogMessage }}</p>
      <ul v-else class="package-list">
        <li v-for="item in packages" :key="item.id" class="package-row">
          <RouterLink class="package-row-link" :to="{ name: 'package', params: { packageId: item.id } }">
            <h3>{{ item.id }}</h3>
            <span>{{ item.versions.length }} {{ item.versions.length === 1 ? 'version' : 'versions' }}</span>
          </RouterLink>
          <code>{{ item.versions.at(-1) }}</code>
        </li>
      </ul>
    </section>
  </main>
</template>
