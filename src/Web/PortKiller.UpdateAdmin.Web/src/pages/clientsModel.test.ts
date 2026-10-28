import { describe, expect, it } from 'vitest';
import { compareVersion, maskInstallationId, reportResult } from './clientsModel';

describe('client version display', () => {
  it('compares numeric versions and normalizes missing components', () => {
    expect(compareVersion('1.2.3.0', '1.2.3')).toBe('与正式版本一致');
    expect(compareVersion('1.9.0', '1.10.0')).toBe('低于正式版本');
    expect(compareVersion('2.0', '1.10')).toBe('高于正式版本');
    expect(compareVersion('1.0')).toBe('尚无正式版本');
  });
  it('masks installation IDs and names outcomes', () => {
    expect(maskInstallationId('1234567890abcdef')).toBe('1234…cdef');
    expect(reportResult('rolled_back')).toBe('已回滚');
  });
});
