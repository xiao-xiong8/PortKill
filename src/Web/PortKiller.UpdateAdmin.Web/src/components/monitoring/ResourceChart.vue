<script setup lang="ts">
import { computed, inject, ref, watch } from 'vue';
import VChart from 'vue-echarts';
import { use } from 'echarts/core';
import { LineChart } from 'echarts/charts';
import { GridComponent, TooltipComponent } from 'echarts/components';
import { SVGRenderer } from 'echarts/renderers';
import type { EChartsOption } from 'echarts';
import type { ElementEvent } from 'echarts/core';
import { bytes } from './display';
import { chartData, escapeTooltip, nearestPoint, pointText, type Point } from './chartModel';

use([LineChart, GridComponent, TooltipComponent, SVGRenderer]);
const props = defineProps<{ points: Point[]; network?: boolean; now: number }>();
const dark = inject('portkill.dark', ref(false));
const chart = ref<InstanceType<typeof VChart>>();
const selectedAt = ref<number>();
const visible = computed(() => props.points.filter(point => point.timestampUtcMs >= props.now - 900_000 && point.timestampUtcMs <= props.now));
const selected = computed(() => selectedAt.value == null ? undefined : nearestPoint(visible.value, selectedAt.value));
watch(visible, points => {
  if (selectedAt.value != null && (!points.length || selectedAt.value < points[0].timestampUtcMs)) {
    selectedAt.value = undefined; chart.value?.dispatchAction({ type: 'hideTip' });
  }
});
const description = computed(() => selectedAt.value == null ? '移动鼠标、触摸图表或使用左右方向键查看采样值。' : pointText(selected.value, !!props.network));
function hideTooltip() { chart.value?.dispatchAction({ type: 'hideTip' }); }
function pointer(event: ElementEvent) {
  const instance = chart.value, position = [event.offsetX, event.offsetY];
  if (!instance?.containPixel({ gridIndex: 0 }, position)) { hideTooltip(); return; }
  // ECharts axis tooltips snap to a series point even with snap:false. Use the
  // actual cursor time so a long collection gap cannot report a distant sample.
  selectedAt.value = Number(instance.convertFromPixel({ xAxisIndex: 0 }, event.offsetX));
  hideTooltip();
  instance.dispatchAction({ type: 'showTip', x: event.offsetX, y: event.offsetY });
}
function keydown(event: KeyboardEvent) {
  if (event.key === 'Escape') { selectedAt.value = undefined; chart.value?.dispatchAction({ type: 'hideTip' }); return; }
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
  event.preventDefault();
  const points = visible.value;
  if (!points.length) return;
  let index = selected.value ? points.indexOf(selected.value) : points.length - 1;
  index = Math.max(0, Math.min(points.length - 1, index + (event.key === 'ArrowLeft' ? -1 : 1)));
  selectedAt.value = points[index].timestampUtcMs;
  chart.value?.dispatchAction({ type: 'showTip', seriesIndex: 0, dataIndex: chartData(points, props.network ? 'rxBytesPerSec' : 'cpuPercent', props.network).findIndex(item => item[0] === selectedAt.value) });
}
const option = computed<EChartsOption>(() => {
  const colors = dark.value ? ['#60a5fa', '#2dd4bf'] : ['#2563eb', '#0f766e'];
  const text = dark.value ? '#b7c3d4' : '#526175';
  const names = props.network ? ['接收', '发送'] : ['CPU', '内存'];
  const keys = props.network ? ['rxBytesPerSec', 'txBytesPerSec'] as const : ['cpuPercent', 'memoryPercent'] as const;
  return {
    animation: false, color: colors,
    grid: { left: props.network ? 80 : 46, right: 18, top: 18, bottom: 34 },
    textStyle: { fontFamily: 'system-ui, sans-serif', color: text },
    tooltip: {
      trigger: 'axis', triggerOn: 'none', confine: true, className: 'monitor-tooltip',
      backgroundColor: dark.value ? '#17202f' : '#ffffff', borderColor: dark.value ? '#455268' : '#cbd5e1',
      textStyle: { color: dark.value ? '#e2e8f0' : '#1e293b', fontSize: 12 },
      axisPointer: { type: 'line', snap: false },
      formatter: () => escapeTooltip(pointText(selected.value, !!props.network)),
    },
    xAxis: { type: 'time', min: props.now - 900_000, max: props.now, splitNumber: 3, axisPointer: { snap: false },
      axisLabel: { color: text, hideOverlap: true, formatter: (value: number) => new Date(value).toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit', hour12: false }) },
      axisLine: { lineStyle: { color: dark.value ? '#455268' : '#cbd5e1' } }, axisTick: { show: false } },
    yAxis: { type: 'value', min: 0, max: props.network ? value => Math.max(1, value.max) : 100,
      interval: props.network ? undefined : 25, splitNumber: 4,
      axisLabel: { color: text, formatter: (value: number) => props.network ? `${bytes(value)}/s` : `${value}%` },
      splitLine: { lineStyle: { color: dark.value ? '#334155' : '#e2e8f0' } } },
    series: keys.map((key, index) => ({ name: names[index], type: 'line', data: chartData(visible.value, key, props.network),
      connectNulls: false, smooth: false, showSymbol: visible.value.length === 1, symbolSize: 5,
      lineStyle: { width: 2 }, emphasis: { focus: 'series' } })),
  };
});
</script>
<template>
  <section class="surface monitor-chart">
    <div class="section-heading"><h2>{{ network ? '网络收发' : 'CPU 与内存' }}</h2><span class="muted">最近 15 分钟</span></div>
    <div class="chart-legend"><span class="chart-first">{{ network ? '接收' : 'CPU' }}</span><span class="chart-second">{{ network ? '发送' : '内存' }}</span></div>
    <div class="monitor-chart-control" tabindex="0" :aria-label="network ? '网络收发图表，左右方向键查询采样' : 'CPU 与内存图表，左右方向键查询采样'" @keydown="keydown">
      <VChart ref="chart" class="resource-echart" :option="option" :init-options="{ renderer: 'svg' }" autoresize @zr:mousemove="pointer" @zr:click="pointer" @zr:globalout="hideTooltip" />
    </div>
    <p v-if="!visible.length" class="muted">等待采样，断流处不连接曲线。</p>
    <p class="chart-readout muted" :aria-live="selectedAt == null ? 'off' : 'polite'">{{ description }}</p>
  </section>
</template>
