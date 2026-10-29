import { describe, expect, it } from 'vitest';
import type { components } from '../../api/contracts.generated';
import { alertObservation, fresh } from './display';

type Monitoring = components['schemas']['MonitoringResponse'];
type Alert = components['schemas']['MonitoringAlert'];
function response(): Monitoring {
  return { status: 'ok', servedAtUtcMs: 100_000, alerts: [], manifest: { status: 'healthy', version: '1.2.3', daysRemaining: 30 },
    snapshot: { schemaVersion: 1, collectorStartedAtUtcMs: 1, generatedAtUtcMs: 100_000, series: [],
      resources: { cpu: { observedAtUtcMs: 100_000, error: null, percent: 20 },
        memory: { observedAtUtcMs: 100_000, error: null, totalBytes: 100, usedBytes: 20 },
        network: { observedAtUtcMs: 100_000, error: null, interface: 'eth0', rxBytesPerSec: 10, txBytesPerSec: 20 },
        disks: [{ label: '业务磁盘', observedAtUtcMs: 100_000, error: null, totalBytes: 100, freeBytes: 5 }] },
      docker: { observedAtUtcMs: 100_000, error: null, services: [] },
      appChecks: [{ name: 'notifier', observedAtUtcMs: 100_000, error: null, status: 'failed',
        httpStatus: 200, database: null, manifestStatus: null, manifestValid: false }] } };
}
const alert = (code: string, source: string): Alert => ({ code, source, severity: 'critical', message: '检查异常' });

describe('monitoring display', () => {
  it('marks all last-response alerts historical after a disconnect and restores them after refresh', () => {
    const value = response(), item = alert('manifest_expiry', 'manifest');
    expect(alertObservation(value, item, 124_999).expired).toBe(false);
    expect(alertObservation(value, item, 125_000)).toMatchObject({ expired: true, observedAtUtcMs: 100_000 });
    value.servedAtUtcMs = 120_000; value.snapshot!.generatedAtUtcMs = 120_000;
    expect(alertObservation(value, item, 120_000).expired).toBe(false);
  });
  it('ages container, disk and probe alerts against their own observation times', () => {
    const value = response(), snapshot = value.snapshot!;
    snapshot.docker.observedAtUtcMs = 55_001; snapshot.resources.disks[0].observedAtUtcMs = 10_001;
    snapshot.appChecks[0].observedAtUtcMs = 10_001;
    for (const item of [alert('container_state', 'worker'), alert('disk_space', '业务磁盘'),
      alert('application_failed', 'notifier'), alert('notifier_manifest', 'notifier')]) {
      expect(alertObservation(value, item, 100_000).expired).toBe(false);
      expect(alertObservation(value, item, 100_001).expired).toBe(true);
    }
    snapshot.docker.observedAtUtcMs = 100_002;
    expect(alertObservation(value, alert('container_state', 'worker'), 100_002).expired).toBe(false);
  });
  it('keeps fresh API diagnoses current even when the collector itself is stale', () => {
    const value = response(); value.snapshot!.generatedAtUtcMs = 80_000;
    expect(alertObservation(value, alert('container_state', 'worker'), 100_000).expired).toBe(false);
    expect(alertObservation(value, alert('source_unavailable', 'docker'), 100_000).expired).toBe(false);
    expect(alertObservation(value, alert('manifest_expiry', 'manifest'), 100_000).expired).toBe(false);
    value.snapshot = null;
    expect(alertObservation(value, alert('snapshot_unavailable', 'collector'), 100_000).expired).toBe(false);
  });
  it('does not present missing or errored source observations as current alarms', () => {
    const value = response(); value.snapshot!.docker.error = 'docker_unavailable';
    expect(alertObservation(value, alert('container_state', 'worker'), 100_000).expired).toBe(true);
    expect(alertObservation(value, alert('disk_space', 'missing'), 100_000).expired).toBe(true);
    expect(alertObservation(value, alert('application_failed', 'missing'), 100_000).expired).toBe(true);
  });
  it('ages sources independently instead of invalidating all sources with one global deadline', () => {
    expect(fresh(1000, null, 45_000, 20_000, 34_999)).toBe(true);
    expect(fresh(1000, null, 45_000, 20_000, 35_000)).toBe(true);
    expect(fresh(1000, 'docker_unavailable', 45_000, 20_000, 20_000)).toBe(false);
    expect(fresh(1000, null, 45_000, 45_000, 46_000)).toBe(false);
    expect(fresh(1000, null, 20_000, 1000, 20_999)).toBe(true);
    expect(fresh(1000, null, 20_000, 1000, 21_000)).toBe(false);
  });
});
