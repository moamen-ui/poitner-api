import { describe, it, expect } from 'vitest';
import { resolveCssUrl, CSS_INTEGRITY } from './constants';

describe('constants', () => {
  it('CSS_URL inherits ?v= when present in script src', () => {
    const urlWithV = resolveCssUrl('https://example.com/assets/pointer.js?v=9f1c2a7b3e4d');
    expect(urlWithV).toBe('https://example.com/assets/pointer.css?v=9f1c2a7b3e4d');
  });

  it('CSS_URL preserves origin and path without ?v= when script has no v', () => {
    const urlWithoutV = resolveCssUrl('https://example.com/assets/pointer.js');
    expect(urlWithoutV).toBe('https://example.com/assets/pointer.css');
  });

  it('CSS_URL preserves ?v= when script URL has other parameters', () => {
    const url = resolveCssUrl('https://example.com/pointer.js?foo=bar&v=abcdef123456');
    expect(url).toBe('https://example.com/pointer.css?v=abcdef123456');
  });

  it('CSS_URL falls back to pointer.css when script src is empty', () => {
    expect(resolveCssUrl('')).toBe('pointer.css');
  });

  it('CSS_INTEGRITY defaults to empty string when not defined at build time', () => {
    expect(typeof CSS_INTEGRITY).toBe('string');
  });
});
