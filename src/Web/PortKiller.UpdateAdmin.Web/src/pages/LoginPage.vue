<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import AuthPanel from '../components/AuthPanel.vue';
import { useAuth } from '../state/auth';
import { internalPath } from '../router';
import { appearanceKey, type ThemePreference } from '../state/appearance';
import PelicanScene from '../components/login/PelicanScene.vue';
import LoginSymbols from '../components/login/LoginSymbols.vue';
import type { WeatherChoice } from '../components/login/pelicanMotion';

const auth = useAuth();
const route = useRoute();
const router = useRouter();
const appearance = inject(appearanceKey)!;
const weatherChoice = ref<WeatherChoice>('auto');
const weather = computed(() => weatherChoice.value === 'auto' ? 'clear' : weatherChoice.value);
const themeOptions: { value: ThemePreference; label: string; icon: string }[] = [
  { value: 'system', label: '跟随系统', icon: 'system' },
  { value: 'light', label: '浅色模式', icon: 'sun' },
  { value: 'dark', label: '深色模式', icon: 'moon' },
];

watch(() => auth.full.value, full => {
  if (full) void router.replace(internalPath(route.query.returnTo) ?? '/overview');
});
</script>

<template>
  <div class="login-experience" :data-theme="appearance.dark.value ? 'dark' : 'light'" :data-weather="weather">
    <LoginSymbols />
    <a class="skip-link" href="#main-content">跳到主内容</a>
    <div class="login-layout">
      <PelicanScene v-model:weather-choice="weatherChoice" :dark="appearance.dark.value" />
      <main id="main-content" class="login-side" tabindex="-1">
        <div class="theme-tools" role="group" aria-label="外观模式">
          <button v-for="option in themeOptions" :key="option.value" type="button" class="theme-button"
            :data-theme-choice="option.value" :aria-label="option.label" :title="option.label"
            :aria-pressed="appearance.preference.value === option.value" @click="appearance.setPreference(option.value)">
            <svg class="icon" aria-hidden="true"><use :href="`#login-i-${option.icon}`" /></svg>
          </button>
        </div>
        <AuthPanel />
        <footer class="footer"><svg class="icon footer-lock" aria-hidden="true"><use href="#login-i-lock" /></svg>PortKiller Update Administration<span>·</span>安全管理入口</footer>
      </main>
    </div>
  </div>
</template>
