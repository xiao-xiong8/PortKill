import type { components } from '../../api/contracts.generated';
import { bytes } from './display';

export type Point = components['schemas']['MonitoringPoint'];
export function chartData(points: Point[], key: 'cpuPercent' | 'memoryPercent' | 'rxBytesPerSec' | 'txBytesPerSec', network = false) {
  const data: [number, number | null][] = [];
  let previous: Point | undefined;
  for (const point of points) {
    if (previous && (point.timestampUtcMs - previous.timestampUtcMs > 15_000 || network && point.networkInterface !== previous.networkInterface))
      data.push([previous.timestampUtcMs + 1, null]);
    data.push([point.timestampUtcMs, point[key]]);
    previous = point;
  }
  return data;
}
export function nearestPoint(points: Point[], timestamp: number) {
  let nearest: Point | undefined;
  for (const point of points) if (!nearest || Math.abs(point.timestampUtcMs - timestamp) < Math.abs(nearest.timestampUtcMs - timestamp)) nearest = point;
  return nearest && Math.abs(nearest.timestampUtcMs - timestamp) <= 7500 ? nearest : undefined;
}
export function pointText(point: Point | undefined, network: boolean) {
  if (!point) return '该时段无采样';
  const percent = (value: number | null) => value == null ? '无数据' : `${value.toFixed(1)}%`;
  const speed = (value: number | null) => value == null ? '无数据' : `${bytes(value)}/s`;
  return `${new Date(point.timestampUtcMs).toLocaleString('zh-CN', { hour12: false })}\n` + (network
    ? `接收 ${speed(point.rxBytesPerSec)} · 发送 ${speed(point.txBytesPerSec)}\n网卡 ${point.networkInterface ?? '未知'}`
    : `CPU ${percent(point.cpuPercent)} · 内存 ${percent(point.memoryPercent)}`);
}
export function escapeTooltip(text: string) {
  return text.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;').replaceAll('\n', '<br>');
}
