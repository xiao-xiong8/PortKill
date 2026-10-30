<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue';
import { createPelicanMotion, type SceneWeather, type WeatherChoice } from './pelicanMotion';

const props = defineProps<{ dark: boolean }>();
const weatherChoice = defineModel<WeatherChoice>('weatherChoice', { required: true });
const sceneRef = ref<SVGSVGElement | null>(null);
const paused = ref(false);
let manuallyPaused = false;
let reducedMotion: MediaQueryList | undefined;
let motion: ReturnType<typeof createPelicanMotion> | undefined;
const weather = computed<SceneWeather>(() => weatherChoice.value === 'auto' ? 'clear' : weatherChoice.value);
const weatherOptions = [
  { value: 'auto', label: '自动', icon: 'auto', title: '随深浅主题切换晴日与月夜' },
  { value: 'clear', label: '晴朗', icon: 'sun', title: '晴朗天气' },
  { value: 'cloudy', label: '多云', icon: 'cloud', title: '多云天气' },
  { value: 'rain', label: '小雨', icon: 'rain', title: '小雨天气' },
  { value: 'snow', label: '落雪', icon: 'snow', title: '落雪天气' },
] as const;
const weatherNames = { clear: '晴朗', cloudy: '多云', rain: '小雨', snow: '落雪' };
const weatherLabel = computed(() => `${props.dark ? '夜间' : '日间'} · ${weather.value === 'clear' && props.dark ? '晴夜' : weatherNames[weather.value]}${weatherChoice.value === 'auto' ? ' / 随主题' : ''}`);
const caption = computed(() => ({
  clear: props.dark ? '月色温柔，前路依然明亮。' : '有风，有阳光，刚好出发。',
  cloudy: '云慢慢走，我们继续向前。', rain: '听雨，也听车轮轻轻转。', snow: '把冬日，骑成一首慢慢的诗。',
})[weather.value]);

function togglePaused() { manuallyPaused = true; paused.value = !paused.value; }
function onReducedMotion() { if (!manuallyPaused && reducedMotion) paused.value = reducedMotion.matches; }
watch(weather, value => motion?.setWeather(value));
watch(paused, value => motion?.setPaused(value));
onMounted(() => {
  reducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)');
  paused.value = reducedMotion.matches;
  reducedMotion.addEventListener('change', onReducedMotion);
  if (sceneRef.value) motion = createPelicanMotion(sceneRef.value, weather.value, paused.value);
});
onBeforeUnmount(() => {
  reducedMotion?.removeEventListener('change', onReducedMotion);
  motion?.dispose();
  motion = undefined;
});
</script>

<template>
<section class="world" aria-label="鹈鹕骑行与天气场景">
      <div class="pelican-brand">
        <svg class="pelican-brand-mark" viewBox="0 0 32 32" fill="none" aria-hidden="true"><path d="M5 8.5 16 3l11 5.5V23l-11 6L5 23Z" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/><path d="m5 8.5 11 6 11-6M16 14.5V29m-5-18 11-6" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round"/></svg>
        <span class="pelican-brand-name">PortKiller</span><span class="pelican-brand-divider"></span><span class="pelican-brand-subtitle">更新管理</span>
      </div>
      <div class="scene-copy">
        <p class="eyebrow">A LITTLE FURTHER, TOGETHER</p>
        <h2>让每一次更新，<br>轻松向前。</h2>
        <p class="scene-caption" id="pelican-sceneCaption">{{ caption }}</p>
      </div>
      <svg id="pelican-scene" ref="sceneRef" class="scene" viewBox="0 0 800 900" preserveAspectRatio="xMidYMid meet" role="img" aria-labelledby="pelican-sceneTitle pelican-sceneDescription">
        <title id="pelican-sceneTitle">海岸边骑自行车的鹈鹕</title>
        <desc id="pelican-sceneDescription">一只长嘴白色鹈鹕沿岸边的陆地自行车道骑行，轮胎贴地滚动，双脚随踏板交替踩踏，身体与头颈轻轻摆动。可切换昼夜、晴天、多云、下雨和下雪。</desc>
        <defs>
          <linearGradient id="pelican-sky" x2="0" y2="1"><stop class="sky-stop-top" offset="0"/><stop class="sky-stop-bottom" offset="1"/></linearGradient>
          <radialGradient id="pelican-sunGlow"><stop stop-color="#fff7d5" stop-opacity=".7"/><stop offset="1" stop-color="#fff7d5" stop-opacity="0"/></radialGradient>
          <radialGradient id="pelican-moonGlow"><stop stop-color="#dceee7" stop-opacity=".14"/><stop offset="1" stop-color="#dceee7" stop-opacity="0"/></radialGradient>
          <linearGradient id="pelican-pouchShade" x2="0" y2="1"><stop stop-color="var(--pouch)"/><stop offset="1" stop-color="var(--beak)"/></linearGradient>
          <g id="pelican-cloudShape"><path d="M0 20c0-11 10-18 20-15C25-9 48-13 60 0c15-5 30 5 30 18h12c12 0 15 16 2 16H7c-12 0-16-13-7-14Z"/></g>
          <!-- Paint shared shapes through attributes so SVG use instances retain their styles. -->
          <g id="pelican-spokePattern" fill="none" stroke="var(--spoke)" stroke-width="1" stroke-opacity=".68"></g>
          <g id="pelican-wheelRings"><circle class="tire" r="105" fill="none" stroke="var(--rubber)" stroke-width="12"/><circle r="103.7" fill="none" stroke="var(--sidewall)" stroke-width="3.2"/><circle r="98" fill="none" stroke="var(--rim)" stroke-width="3.4"/><circle r="94.8" fill="none" stroke="var(--bike-highlight)" stroke-width=".75" opacity=".6"/></g>
          <g id="pelican-wheelRotor"><use href="#pelican-spokePattern"/><circle r="108.3" fill="none" stroke="var(--rim)" stroke-width="1.2" stroke-dasharray="1.5 9" stroke-opacity=".25"/><path d="M0-94v6" stroke="var(--rubber)" stroke-width="2.2" stroke-linecap="round"/><path d="m61 38 8 5" stroke="var(--beak-top)" stroke-width="4" stroke-linecap="round" opacity=".85"/></g>
          <g id="pelican-wheelHub"><circle r="9" fill="var(--rubber)"/><circle r="6.5" fill="var(--rim)"/><circle r="3" fill="var(--bike)"/><circle cx="-1.7" cy="-2" r="1.3" fill="#fff" opacity=".75"/></g>
          <g id="pelican-rimLight"><path d="M-87-45a98 98 0 0 1 96-53" fill="none" stroke="#fff" stroke-width="1.7" stroke-linecap="round" opacity=".36"/></g>
          <pattern id="pelican-roadTexture" width="180" height="70" patternUnits="userSpaceOnUse"><g fill="var(--road-edge)" opacity=".5"><ellipse cx="21" cy="16" rx="2.5" ry=".9"/><ellipse cx="117" cy="32" rx="3.2" ry=".8"/><ellipse cx="68" cy="59" rx="1.7" ry=".7"/><ellipse cx="164" cy="65" rx="1.9" ry=".8"/></g></pattern>
          <g id="pelican-footShape"><path fill="var(--foot-color, var(--leg))" stroke="var(--leg-back)" stroke-width="1" stroke-linejoin="round" d="M-8-16c-4 4-2 9-8 12l-7 4c-2 2-1 4 2 4h41c4 0 5-3 1-5L8-12c-3-4-9-6-16-4Z"/><path fill="none" stroke="#9f763f" stroke-width="1" opacity=".6" d="m-5-8 11 9M1-10l14 11M-8-6l4 7"/></g>
        </defs>
        <rect x="-800" y="-800" width="2400" height="2500" fill="url(#pelican-sky)"/>
        <g class="sun" aria-hidden="true"><circle cx="641" cy="210" r="115" fill="url(#pelican-sunGlow)"/><circle cx="641" cy="210" r="37" fill="#f3ce82"/><circle cx="641" cy="210" r="49" fill="none" stroke="#f3ce82" stroke-opacity=".2"/></g>
        <g class="moon" aria-hidden="true"><circle cx="641" cy="210" r="115" fill="url(#pelican-moonGlow)"/><path d="M653 174a38 38 0 1 0 25 62 36 36 0 0 1-25-62Z" fill="#e8e9ce"/><circle cx="623" cy="218" r="3" fill="#c6d2c4" opacity=".6"/><circle cx="636" cy="236" r="5" fill="#c6d2c4" opacity=".35"/></g>
        <g class="stars" fill="#e5eee4" aria-hidden="true"><circle cx="367" cy="119" r="1.8"/><circle cx="516" cy="94" r="1.3"/><circle cx="713" cy="106" r="1.8"/><circle cx="568" cy="267" r="1.3"/><circle cx="740" cy="343" r="1.6"/><circle cx="507" cy="203" r="1.4"/><circle cx="334" cy="295" r="1.2"/><circle cx="680" cy="377" r="1.3"/><path d="m569 154 1.4 4.6 4.6 1.4-4.6 1.4-1.4 4.6-1.4-4.6-4.6-1.4 4.6-1.4Z"/><path d="m732 251 1 3.6 3.6 1-3.6 1-1 3.6-1-3.6-3.6-1 3.6-1Z"/></g>
        <g class="clouds" aria-hidden="true">
          <g id="pelican-cloudOne"><use href="#pelican-cloudShape" transform="translate(505 354) scale(.9)"/></g>
          <g id="pelican-cloudTwo"><use href="#pelican-cloudShape" transform="translate(92 322) scale(.58)"/></g>
          <g class="extra-cloud"><use href="#pelican-cloudShape" transform="translate(420 174) scale(1.35)"/><use href="#pelican-cloudShape" transform="translate(685 309) scale(1.5)"/><use href="#pelican-cloudShape" transform="translate(16 425) scale(1.15)"/></g>
        </g>
        <g class="scenery" aria-hidden="true">
          <path d="M-300 664c197-56 273-11 402-48 137-39 172-15 271 22 106 39 169-46 286-30 102 15 216 2 441-41v245H-300Z" fill="var(--hill-far)" opacity=".48"/>
          <path d="M-300 721c172-57 285-64 418-34s218-42 335-27 184-27 320-9 231 20 327-4v185H-300Z" fill="var(--water)"/>
          <path d="M-300 723c253-17 290-17 412-6 152 14 243 26 400 7 113-13 249-7 588-27v136H-300Z" fill="var(--hill-near)" opacity=".38"/>
          <g stroke="var(--cloud)" stroke-width="2" stroke-linecap="round" opacity=".28"><path d="M637 672h39m22 0h11M38 686h42m13 0h10M539 680h24"/></g>
          <path id="pelican-land" d="M-300 703C20 690 113 684 276 696S623 682 1100 695V2500H-300Z" fill="var(--shore)"/>
          <path d="M-300 703C20 690 113 684 276 696S623 682 1100 695" fill="none" stroke="var(--cloud)" stroke-width="2" opacity=".3"/>
          <path id="pelican-roadSurface" d="M-300 721C110 714 601 712 1100 721V850C600 839 110 842-300 851Z" fill="var(--road)"/>
          <path d="M-300 721C110 714 601 712 1100 721M-300 851C110 842 600 839 1100 850" fill="none" stroke="var(--road-edge)" stroke-width="2" opacity=".65"/>
          <g id="pelican-roadDetails"><rect x="-500" y="726" width="1900" height="106" fill="url(#pelican-roadTexture)"/></g>
          <g id="pelican-roadLines" fill="none" stroke="var(--road-line)" stroke-width="3" stroke-dasharray="52 156" opacity=".72"><path d="M-500 802H1400"/></g>
          <g fill="none" stroke="var(--scene-muted)" stroke-width="2" stroke-linecap="round" opacity=".55"><path d="M87 711q3-17-4-23m4 16q8-10 15-9M718 709q-1-17 7-24m-7 19q-7-11-13-10"/></g>
          <g fill="#d3b97c" opacity=".8"><circle cx="83" cy="686" r="3"/><circle cx="103" cy="694" r="2.8"/><circle cx="726" cy="684" r="3"/></g>
          <path d="m61 868 2-13m-2 8-6-7m674 8 3-12m-3 6-5-4" fill="none" stroke="var(--scene-muted)" stroke-width="1.5" stroke-linecap="round" opacity=".45"/>
        </g>
        <g class="wind" id="pelican-wind" aria-hidden="true"><path d="M157 425h49m-65 16h33M612 488h49m-34 12h18"/></g>
        <g id="pelican-bicycle" aria-hidden="true">
          <g id="pelican-groundShadows" fill="var(--shadow)"><ellipse id="pelican-bikeShadow" cx="413" cy="749" rx="218" ry="9" opacity=".085"/><ellipse cx="248" cy="749.5" rx="29" ry="4" opacity=".18"/><ellipse cx="578" cy="749.5" rx="29" ry="4" opacity=".18"/></g>
          <g id="pelican-rearWheel" transform="translate(248 638)"><use href="#pelican-wheelRings"/><g id="pelican-rearSpokes"><use href="#pelican-wheelRotor"/></g><use href="#pelican-rimLight"/><use href="#pelican-wheelHub"/></g>
          <g id="pelican-frontWheel" transform="translate(578 638)"><use href="#pelican-wheelRings"/><g id="pelican-frontSpokes"><use href="#pelican-wheelRotor"/></g><use href="#pelican-rimLight"/><use href="#pelican-wheelHub"/></g>
          <g id="pelican-rearDrivetrain"><path id="pelican-backCrank" fill="none" stroke="var(--rubber)" stroke-width="6" stroke-linecap="round"/><path id="pelican-backLeg" class="leg back-leg"/><g id="pelican-backPedal"><rect class="pedal" x="-23" y="-3" width="46" height="6" rx="2.5"/></g><g id="pelican-backFoot" style="--foot-color: var(--leg-back)"><use href="#pelican-footShape" transform="translate(0 -7)"/></g></g>
          <g fill="none" stroke="var(--rubber)" stroke-width="2" opacity=".72"><path d="M248 627 395 608a20 20 0 0 1 5 39l-150 3a12 12 0 0 1-2-23Z"/><path id="pelican-chainLinks" d="M248 627 395 608a20 20 0 0 1 5 39l-150 3a12 12 0 0 1-2-23Z" stroke="var(--rim)" stroke-width="1" stroke-dasharray="1.5 4" opacity=".6"/></g>
          <path class="bike-frame" d="m248 638 102-132 48 121-150 11m102-132 166-6-118 127m118-127 62 138"/>
          <path d="m359 511 148-6m-105 109 105-109" fill="none" stroke="var(--bike-highlight)" stroke-width="2.5" stroke-linecap="round" opacity=".7"/>
          <path d="m350 506-4-17m170 11-8-27 25-34" fill="none" stroke="var(--rubber)" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/>
          <path d="M323 485q25-5 48-1c9 2 8 10-2 11h-37c-11 0-18-7-9-10Z" fill="var(--rubber)"/>
          <path d="M534 439h18q16 0 12 15l-3 11" fill="none" stroke="var(--rubber)" stroke-width="7" stroke-linecap="round"/>
          <path d="M553 440h5q12 2 6 14l-2 7" fill="none" stroke="var(--bike-highlight)" stroke-width="5" stroke-linecap="round"/>
          <path d="M499 477q57 5 62 68" fill="none" stroke="var(--rubber)" stroke-width="1.4" opacity=".7"/>
          <path d="M171 596a94 94 0 0 1 137-31M506 565a94 94 0 0 1 143 35" fill="none" stroke="var(--bike)" stroke-width="5" stroke-linecap="round" opacity=".65"/>
          <circle cx="398" cy="627" r="20" fill="var(--bike)" stroke="var(--rubber)" stroke-width="2"/><circle cx="398" cy="627" r="13" fill="none" stroke="var(--rim)" stroke-width="1.5" opacity=".7"/>
          <g id="pelican-chainringDetail" fill="var(--rubber)" opacity=".7"><circle cx="398" cy="616" r="2.5"/><circle cx="407.5" cy="632.5" r="2.5"/><circle cx="388.5" cy="632.5" r="2.5"/></g>
          <path id="pelican-frontCrank" fill="none" stroke="var(--rubber)" stroke-width="6" stroke-linecap="round"/>
          <path id="pelican-frontLeg" class="leg front-leg"/>
          <g id="pelican-frontPedal"><rect class="pedal" x="-23" y="-3" width="46" height="6" rx="2.5"/></g><g id="pelican-frontFoot"><use href="#pelican-footShape" transform="translate(0 -7)"/></g>
          <circle cx="398" cy="627" r="4" fill="var(--rim)"/>
          <g id="pelican-birdBody" class="bird">
            <g id="pelican-tailFeathers"><path d="m303 398-48-18 16 31-25-6 29 25 34-6Z" fill="var(--bird-shade)"/></g>
            <path d="M331 350c-36 9-57 43-42 78 16 39 69 53 107 31 38-22 52-56 25-81-25-24-58-38-90-28Z" fill="var(--bird)"/>
            <path d="M310 442c27 18 64 16 88-4l-2 21c-38 22-76 5-86-17Z" fill="var(--bird-shade)"/>
            <g id="pelican-pelicanHead">
            <path d="M392 391c29-23 18-56 33-92 11-26 25-43 48-39 26 3 40 21 30 40-7 15-26 22-39 41-13 18-8 46-28 69-10 11-26 9-44-19Z" fill="var(--bird)"/>
            <path d="M447 327c-14 21-9 57-29 71l15 13c20-23 16-50 31-70 9-13 18-19 24-26-12 0-27 3-41 12Z" fill="var(--bird-shade)"/>
            <path d="m451 262-8-15 14 10-1-15 10 17" fill="var(--bird)" stroke="var(--bird-line)" stroke-width="1" stroke-linejoin="round"/>
            <path d="M492 288c35 3 78 8 125 13l-13 10c-14 31-42 34-74 18-18-8-28-23-38-41Z" fill="url(#pelican-pouchShade)"/>
            <path d="M495 289c41-1 85 3 127 10 6 1 8 4 4 8l-8 4 1-6c-48 0-88-2-126-7Z" fill="var(--beak-top)"/>
            <path d="m498 298 119 7" fill="none" stroke="#b38345" stroke-width="1.5" opacity=".7"/>
            <path d="M504 303q17 28 52 30" fill="none" stroke="#b68a51" stroke-width="1" opacity=".35"/>
            <g id="pelican-pelicanEye"><ellipse cx="477" cy="280" rx="8" ry="8.5" fill="var(--bird-shade)"/><circle cx="479" cy="280" r="4.2" fill="#253e4c"/><circle cx="480.4" cy="278.7" r="1.35" fill="#fff"/></g>
            <path d="m468 266 9-1" fill="none" stroke="var(--bird-line)" stroke-width="1.5" stroke-linecap="round"/>
            </g>
            <path d="m423 337-30-13-13 5 17 5-12 6 35 7Z" fill="var(--bike)"/>
            <path id="pelican-scarfTail" d="m421 341-31 6-19-7 15 16 34-7Z" fill="var(--bike-highlight)"/>
            <path d="m421 331 24 10-4 11-24-11Z" fill="var(--bike)"/>
            <g id="pelican-restingWing">
            <path d="M330 367c28-12 62 8 77 39 8 17-1 29-17 28-36 0-71-25-60-67Z" fill="var(--bird-wing)"/>
            <path d="M342 377q6 28 37 40m-44-25q8 23 36 30m-26-49q14 19 36 25" fill="none" stroke="var(--bird-line)" stroke-width="2.5" stroke-linecap="round"/>
            </g>
            <path d="M314 428q10 10 20 13m7 2 13 3" fill="none" stroke="var(--bird-line)" stroke-width="1.5" stroke-linecap="round" opacity=".6"/>
          </g>
          <path id="pelican-steeringWing" fill="var(--bird-shade)" stroke="var(--bird-line)" stroke-width="1.2" stroke-linejoin="round"/>
          <path id="pelican-wingDetail" fill="none" stroke="var(--bird-line)" stroke-width="1.6" stroke-linecap="round"/>
        </g>
        <g class="day-sparkle" fill="none" stroke="var(--scene-muted)" stroke-width="1.5" opacity=".45" aria-hidden="true"><path d="M180 532v8m-4-4h8M636 416v8m-4-4h8"/></g>
        <g id="pelican-weatherEffects" aria-hidden="true" pointer-events="none"></g>
      </svg>
      <div class="weather-dock">
        <div class="weather-heading"><span class="weather-dot"></span><span id="pelican-weatherLabel" aria-live="polite">{{ weatherLabel }}</span></div>
        <div class="weather-options" role="group" aria-label="场景天气">
          <button v-for="option in weatherOptions" :key="option.value" type="button" class="weather-button"
            :data-weather-choice="option.value" :aria-pressed="weatherChoice === option.value" :title="option.title"
            @click="weatherChoice = option.value">
            <svg class="icon" aria-hidden="true"><use :href="`#login-i-${option.icon}`" /></svg>{{ option.label }}
          </button>
          <button type="button" class="motion-button" :aria-label="paused ? '播放动画' : '暂停动画'"
            :aria-pressed="paused" :title="paused ? '播放动画' : '暂停动画'" @click="togglePaused">
            <svg class="icon" aria-hidden="true"><use :href="paused ? '#login-i-play' : '#login-i-pause'" /></svg>
          </button>
        </div>
      </div>
    </section>
</template>
