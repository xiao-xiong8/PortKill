import { describe, expect, it } from 'vitest';
import { redact, roleLabel, toggleRole } from './securityModel';

describe('security page display helpers', () => {
  it('keeps role changes non-empty and rejects unknown roles', () => {
    expect(toggleRole(['Viewer'], 'Viewer')).toEqual(['Viewer']);
    expect(toggleRole(['Viewer'], 'Uploader')).toEqual(['Viewer', 'Uploader']);
    expect(toggleRole(['Viewer'], 'Root')).toEqual(['Viewer']);
  });

  it('uses safe labels and masks long identifiers', () => {
    expect(roleLabel('Administrator')).toBe('管理员');
    expect(roleLabel('Other')).toBe('Other');
    expect(redact('12345678-1234-1234-1234-123456789abc')).toBe('12345678…9abc');
    expect(redact(null)).toBe('—');
  });
});
