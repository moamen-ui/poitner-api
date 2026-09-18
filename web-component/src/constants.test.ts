import { describe, it, expect } from 'vitest';
import { resolveCssUrl, CSS_INTEGRITY } from './constants';

describe('constants', () => {
  it('CSS_URL inherits ?v= when present in script src', () => {
    const urlWithV = resolveCssUrl('https://example.com/assets/widget.js?v=9f1c2a7b3e4d');
    expect(urlWithV).toBe('https://example.com/assets/widget.css?v=9f1c2a7b3e4d');
  });

  it('CSS_URL preserves origin and path without ?v= when script has no v', () => {
    const urlWithoutV = resolveCssUrl('https://example.com/assets/widget.js');
    expect(urlWithoutV).toBe('https://example.com/assets/widget.css');
  });

  it('CSS_URL preserves ?v= when script URL has other parameters', () => {
    const url = resolveCssUrl('https://example.com/widget.js?foo=bar&v=abcdef123456');
    expect(url).toBe('https://example.com/widget.css?v=abcdef123456');
  });

  it('CSS_URL falls back to widget.css when script src is empty', () => {
    expect(resolveCssUrl('')).toBe('widget.css');
  });

  it('CSS_INTEGRITY defaults to empty string when not defined at build time', () => {
    expect(typeof CSS_INTEGRITY).toBe('string');
  });
});
