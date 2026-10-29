import { describe, expect, it } from 'vitest';
import { chartData, escapeTooltip, nearestPoint, pointText, type Point } from './chartModel';
const point = (at: number, iface = 'eth0'): Point => ({ timestampUtcMs: at, cpuPercent: 0, memoryPercent: 25, rxBytesPerSec: 1024, txBytesPerSec: null, networkInterface: iface });
describe('ECharts observation model', () => {
  it('breaks lines for missing observations, long sampling gaps and interface changes', () => {
    expect(chartData([point(1000), point(20_001)], 'cpuPercent')).toEqual([[1000, 0], [1001, null], [20_001, 0]]);
    expect(chartData([point(1000), point(6000, 'ens3')], 'rxBytesPerSec', true)).toEqual([[1000, 1024], [1001, null], [6000, 1024]]);
    expect(chartData([point(1000)], 'txBytesPerSec')).toEqual([[1000, null]]);
  });
  it('queries real samples and never invents values in long gaps', () => {
    const points = [point(1000), point(6000), point(60_000)];
    expect(nearestPoint(points, 5500)).toBe(points[1]);
    expect(nearestPoint(points, 30_000)).toBeUndefined();
    expect(nearestPoint([], 0)).toBeUndefined();
  });
  it('distinguishes zero, missing data and byte units, escaping untrusted labels', () => {
    expect(pointText(point(1000), false)).toContain('CPU 0.0%');
    expect(pointText(point(1000), true)).toContain('接收 1.0 KiB/s · 发送 无数据');
    expect(pointText(undefined, true)).toBe('该时段无采样');
    expect(escapeTooltip('<img onerror="bad">\n&')).toBe('&lt;img onerror=&quot;bad&quot;&gt;<br>&amp;');
  });
});
