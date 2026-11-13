<script setup lang="ts">
import { ref, watch } from 'vue'
import { RouterLink, useRoute } from 'vue-router'

type PackageDetails = {
  id: string
  latestVersion: string
  versions: string[]
  description?: string | null
  author?: string | null
  licence?: string | null
  website?: string | null
  dependencies: string[]
}

const route = useRoute()
const details = ref<PackageDetails | null>(null)
const message = ref('Loading package…')

watch(
  () => route.params.packageId,
  async (value) => {
    const id = String(value ?? '')
    details.value = null
    message.value = 'Loading package…'

    try {
      const response = await fetch(`/api/packages/${encodeURIComponent(id)}`)
      if (response.status === 404) {
        message.value = 'This package could not be found.'
        return
      }
      if (!response.ok) throw new Error(`HTTP ${response.status}`)
      details.value = (await response.json()) as PackageDetails
      message.value = ''
    } catch {
      message.value = 'Package details are unavailable right now.'
    }
  },
  { immediate: true },
)
</script>

<template>
  <main class="page-content package-page">
    <p class="eyebrow"><span class="eyebrow-line"></span> PACKAGE DETAILS</p>
    <RouterLink class="back-link" to="/">← Back to catalog</RouterLink>
    <p v-if="message" class="catalog-message" aria-live="polite">{{ message }}</p>
    <template v-else-if="details">
      <section class="package-heading">
        <div>
          <h1>{{ details.id }}</h1>
          <p v-if="details.description" class="hero-description">{{ details.description }}</p>
        </div>
        <span class="latest-version">{{ details.latestVersion }}</span>
      </section>

      <section class="detail-section" aria-labelledby="metadata-title">
        <h2 id="metadata-title">Package information</h2>
        <dl class="metadata-grid">
          <template v-if="details.author"
            ><dt>Author</dt>
            <dd>{{ details.author }}</dd></template
          >
          <template v-if="details.licence"
            ><dt>Licence</dt>
            <dd>{{ details.licence }}</dd></template
          >
          <template v-if="details.website"
            ><dt>Website</dt>
            <dd>
              <a :href="details.website" target="_blank" rel="noreferrer">{{ details.website }}</a>
            </dd></template
          >
        </dl>
      </section>

      <section class="detail-section" aria-labelledby="versions-title">
        <h2 id="versions-title">
          Versions <span class="count-chip">{{ details.versions.length }}</span>
        </h2>
        <ul class="value-list">
          <li v-for="version in [...details.versions].reverse()" :key="version">
            <code>{{ version }}</code>
          </li>
        </ul>
      </section>

      <section class="detail-section" aria-labelledby="dependencies-title">
        <h2 id="dependencies-title">
          Dependencies <span class="count-chip">{{ details.dependencies.length }}</span>
        </h2>
        <ul v-if="details.dependencies.length" class="value-list">
          <li v-for="dependency in details.dependencies" :key="dependency">
            <code>{{ dependency }}</code>
          </li>
        </ul>
        <p v-else class="muted-copy">This package has no dependencies.</p>
      </section>
    </template>
  </main>
</template>
