<template>
  <div class="display">
    <header class="display-header">
      <img src="/assets/zonnewijzer_logo.png" alt="Zonnewijzer" class="logo">
      <h1>Laadpalen</h1>
    </header>

    <p v-if="loading" class="status">Laden…</p>
    <p v-else-if="error" class="status error">{{ error }}</p>
    <div v-else class="stations" :style="gridStyle">
      <article
        v-for="(station, index) in stations"
        :key="`${station.name}-${index}`"
        :class="['station', station.inUse ? 'busy' : 'free']"
      >
        <h2>{{ station.name }}</h2>
        <p class="state">{{ station.inUse ? 'Bezet' : 'Vrij' }}</p>
        <p v-if="powerText(station)" class="level">{{ powerText(station) }}</p>
        <p v-if="levelText(station)" class="level">{{ levelText(station) }}</p>
        <p v-if="station.inUse && station.userCode" class="code">{{ station.userCode }}</p>
      </article>
    </div>
  </div>
</template>

<script>
import { computed, onMounted, onBeforeUnmount, ref } from 'vue'
import axios from 'axios'

export default {
  name: 'PublicDisplay',
  setup() {
    const stations = ref([])
    const loading = ref(true)
    const error = ref('')
    let timer = null

    const formatWatts = (value) => {
      if (value === null || value === undefined || value === '')
        return ''
      const number = Number(value)
      if (!Number.isFinite(number))
        return ''
      return `${new Intl.NumberFormat('nl-NL', { maximumFractionDigits: 0 }).format(number)} W`
    }

    const formatKwh = (value) => {
      if (value === null || value === undefined || value === '')
        return ''
      const number = Number(value)
      if (!Number.isFinite(number))
        return ''
      return `${new Intl.NumberFormat('nl-NL', { minimumFractionDigits: 1, maximumFractionDigits: 1 }).format(number)} kWh`
    }

    const formatEndedAgo = (value) => {
      const then = new Date(value).getTime()
      if (!Number.isFinite(then))
        return ''
      const minutes = Math.max(0, Math.round((Date.now() - then) / 60000))
      if (minutes < 1)
        return 'zojuist geëindigd'
      if (minutes < 60)
        return `${minutes} min geleden`
      const hours = Math.round(minutes / 60)
      if (hours < 48)
        return hours === 1 ? '1 uur geleden' : `${hours} uur geleden`
      const days = Math.round(hours / 24)
      return days === 1 ? '1 dag geleden' : `${days} dagen geleden`
    }

    const powerText = (station) => station.inUse ? formatWatts(station.powerWatts) : ''

    const levelText = (station) => {
      if (station.inUse)
        return formatKwh(station.energyKwh)
      if (station.endedAtUtc)
        return formatEndedAgo(station.endedAtUtc)
      return ''
    }

    const gridStyle = computed(() => ({
      gridTemplateColumns: `repeat(${Math.max(stations.value.length, 1)}, minmax(0, 1fr))`
    }))

    const load = async () => {
      try {
        const { data } = await axios.get('/api/display/stations')
        stations.value = Array.isArray(data?.stations) ? data.stations : []
        error.value = ''
      } catch {
        error.value = 'De laadpalen zijn even niet beschikbaar.'
      } finally {
        loading.value = false
      }
    }

    onMounted(() => {
      load()
      timer = window.setInterval(load, 20000)
    })

    onBeforeUnmount(() => {
      if (timer != null)
        window.clearInterval(timer)
    })

    return { stations, loading, error, gridStyle, levelText, powerText }
  }
}
</script>

<style scoped>
.display {
  min-height: 100vh;
  box-sizing: border-box;
  padding: 1.5rem 1.25rem 2rem;
  background: #0f172a;
  color: #f8fafc;
  display: flex;
  flex-direction: column;
  gap: 1.25rem;
}

.display-header {
  display: flex;
  align-items: center;
  gap: 1rem;
}

.logo {
  height: 3.5rem;
  width: auto;
}

h1 {
  margin: 0;
  font-size: 2rem;
  font-weight: 700;
}

.status {
  margin: 2rem 0 0;
  font-size: 1.5rem;
}

.status.error {
  color: #fecaca;
}

.stations {
  flex: 1;
  display: grid;
  gap: 1rem;
  min-height: 0;
}

.station {
  border-radius: 1rem;
  padding: 1.25rem;
  display: flex;
  flex-direction: column;
  justify-content: center;
  gap: 0.75rem;
  min-height: 16rem;
}

.station.free {
  background: #15803d;
}

.station.busy {
  background: #b91c1c;
}

h2,
.state,
.level,
.code {
  margin: 0;
  text-align: center;
}

h2 {
  font-size: clamp(1.6rem, 3vw, 2.6rem);
}

.state {
  font-size: clamp(1.4rem, 2.5vw, 2.2rem);
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.level {
  font-size: clamp(1.2rem, 2vw, 1.8rem);
  font-weight: 650;
}

.code {
  font-size: clamp(2rem, 4vw, 3.5rem);
  font-weight: 800;
  letter-spacing: 0.02em;
}

@media (max-width: 800px) {
  .stations {
    grid-template-columns: 1fr !important;
  }
}
</style>
