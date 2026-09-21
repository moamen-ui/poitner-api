import { describe, it, expect } from 'vitest';
import { validateFieldValue, hostMatches } from './fields';
import type { CommentFieldDefinition } from './types';

describe('fields', () => {
  describe('hostMatches', () => {
    it('matches exact', () => {
      expect(hostMatches('acme.atlassian.net', 'acme.atlassian.net')).toBe(true);
      expect(hostMatches('acme.atlassian.net', 'other.atlassian.net')).toBe(false);
    });
    it('matches wildcard including bare domain', () => {
      expect(hostMatches('test.example.com', '*.example.com')).toBe(true);
      expect(hostMatches('example.com', '*.example.com')).toBe(true);
      expect(hostMatches('sub.test.example.com', '*.example.com')).toBe(true);
      expect(hostMatches('anotherexample.com', '*.example.com')).toBe(false);
    });
  });

  describe('validateFieldValue', () => {
    const textDef: CommentFieldDefinition = { key: 't', label: 'T', type: 1, enabled: true };
    const urlDef: CommentFieldDefinition = { key: 'u', label: 'U', type: 2, enabled: true, allowedHosts: ['*.atlassian.net'] };
    const selectDef: CommentFieldDefinition = { key: 's', label: 'S', type: 3, enabled: true, options: ['A', 'B'] };

    it('allows empty', () => {
      expect(validateFieldValue(textDef, '')).toBeNull();
      expect(validateFieldValue(textDef, '   ')).toBeNull();
    });

    it('validates Text', () => {
      expect(validateFieldValue(textDef, 'hello')).toBeNull();
      expect(validateFieldValue(textDef, 'a'.repeat(501))).toBe('fields.tooLong');
    });

    it('validates Url', () => {
      expect(validateFieldValue(urlDef, 'https://acme.atlassian.net/browse/X')).toBeNull();
      expect(validateFieldValue(urlDef, 'http://acme.atlassian.net/')).toBeNull();
      
      // Invalid scheme
      expect(validateFieldValue(urlDef, 'ftp://acme.atlassian.net/')).toBe('fields.invalidUrl');
      expect(validateFieldValue(urlDef, 'not-a-url')).toBe('fields.invalidUrl');
      
      // User info
      expect(validateFieldValue(urlDef, 'https://user:pass@acme.atlassian.net')).toBe('fields.invalidUrl');
      
      // Host not allowed
      expect(validateFieldValue(urlDef, 'https://evil.com')).toBe('fields.hostNotAllowed');
      
      // Too long
      expect(validateFieldValue(urlDef, 'https://acme.atlassian.net/' + 'a'.repeat(2000))).toBe('fields.tooLong');
    });

    it('validates Select', () => {
      expect(validateFieldValue(selectDef, 'A')).toBeNull();
      expect(validateFieldValue(selectDef, 'B')).toBeNull();
      expect(validateFieldValue(selectDef, 'C')).toBe('fields.invalidOption');
    });
  });
});
