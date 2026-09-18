import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import {
  startPageContextCapture,
  stopPageContextCapture,
  getPageContextPayload,
  resetPageContextBuffers,
  rawFetch,
} from './pagecontext';

// Minimal XMLHttpRequest stand-in: the patch only touches prototype.open/send and listens for
// `loadend`, so this is all a test needs to drive it deterministically.
class FakeXhr {
  status = 0;
  private listeners: Record<string, Array<() => void>> = {};
  static nextStatus = 200;
  open(_method: string, _url: string) {}
  send(_body?: unknown) {
    this.status = FakeXhr.nextStatus;
    (this.listeners['loadend'] || []).forEach((l) => l());
  }
  addEventListener(type: string, cb: () => void) {
    (this.listeners[type] ||= []).push(cb);
  }
}

function okResponse(status = 200): Response {
  return { ok: status < 400, status } as Response;
}

describe('pagecontext', () => {
  const realFetch = window.fetch;
  const realXhr = (globalThis as any).XMLHttpRequest;
  let fetchMock: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    fetchMock = vi.fn(async (input: any) => {
      const url = typeof input === 'string' ? input : input.url;
      if (url.includes('/fail')) return okResponse(500);
      if (url.includes('/boom')) throw new TypeError('network down');
      return okResponse(200);
    });
    window.fetch = fetchMock as any;
    (globalThis as any).XMLHttpRequest = FakeXhr;
    FakeXhr.nextStatus = 200;
    resetPageContextBuffers();
  });

  afterEach(() => {
    stopPageContextCapture();
    window.fetch = realFetch;
    (globalThis as any).XMLHttpRequest = realXhr;
  });

  it('returns null before capture starts, and an EMPTY payload (not null) once started', () => {
    expect(getPageContextPayload()).toBeNull();
    startPageContextCapture();
    const p = getPageContextPayload();
    expect(p).not.toBeNull();
    expect(p!.sessionId).toBeTruthy();
    expect(p!.consoleEntries).toEqual([]);
    expect(p!.networkEntries).toEqual([]);
  });

  it('records failed and errored fetches but not successful ones', async () => {
    startPageContextCapture();
    await window.fetch('https://app.example.com/api/ok?x=1');
    await window.fetch('https://app.example.com/api/fail?x=1');
    await window.fetch('https://app.example.com/api/boom').catch(() => {});
    const net = getPageContextPayload()!.networkEntries;
    expect(net.map((e) => [e.url, e.statusCode])).toEqual([
      ['https://app.example.com/api/fail', 500],
      ['https://app.example.com/api/boom', null],
    ]);
  });

  it('does not exclude same-origin or Pointer-server requests by origin (dogfooding case)', async () => {
    startPageContextCapture('https://api.pointer.example', 'https://api.pointer.example/widget.js');
    await window.fetch(`${window.location.origin}/fail`);
    await window.fetch('https://api.pointer.example/api/fail');
    expect(getPageContextPayload()!.networkEntries).toHaveLength(2);
  });

  it("never records the widget's own traffic sent through rawFetch", async () => {
    startPageContextCapture();
    await rawFetch('https://api.pointer.example/api/fail');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(getPageContextPayload()!.networkEntries).toEqual([]);
  });

  it('records failed XMLHttpRequest calls (Angular HttpClient / axios default transport)', () => {
    startPageContextCapture();
    const ok = new (globalThis as any).XMLHttpRequest();
    ok.open('GET', 'https://app.example.com/api/list?page=2');
    ok.send();
    FakeXhr.nextStatus = 404;
    const bad = new (globalThis as any).XMLHttpRequest();
    bad.open('get', 'https://app.example.com/api/comments?page=1');
    bad.send();
    FakeXhr.nextStatus = 0; // network error / CORS-blocked
    const dead = new (globalThis as any).XMLHttpRequest();
    dead.open('POST', '/api/save');
    dead.send('{}');
    const net = getPageContextPayload()!.networkEntries;
    expect(net.map((e) => [e.method, e.url, e.statusCode])).toEqual([
      ['GET', 'https://app.example.com/api/comments', 404],
      ['POST', '/api/save', null],
    ]);
  });

  it('records console.error/warn, uncaught errors and unhandled rejections, skipping its own logs', () => {
    // Quiet base console so the patched wrappers have a silent target to forward to.
    const realErr = console.error;
    const realWarn = console.warn;
    console.error = () => {};
    console.warn = () => {};
    try {
      startPageContextCapture();
      console.error('[pointer-feedback] internal noise');
      console.error('Cannot read properties of undefined', new Error('boom'));
      console.error('Cannot read properties of undefined', new Error('boom'));
      console.warn('deprecated API');
      window.dispatchEvent(new ErrorEvent('error', { message: 'Script error', error: new Error('kaboom'), filename: 'main.js', lineno: 10, colno: 5 }));
      const rejection = new Event('unhandledrejection') as any;
      rejection.reason = new Error('no handler');
      window.dispatchEvent(rejection);
      const entries = getPageContextPayload()!.consoleEntries;
      expect(entries.map((e) => [e.level, e.message, e.count])).toEqual([
        ['error', 'Cannot read properties of undefined boom', 2],
        ['warn', 'deprecated API', 1],
        ['error', 'Uncaught Script error (main.js:10:5)', 1],
        ['error', 'Unhandled promise rejection: no handler', 1],
      ]);
      expect(entries[0].stack).toContain('boom');
      expect(entries[2].stack).toContain('kaboom');
      expect(entries[3].stack).toContain('no handler');
    } finally {
      stopPageContextCapture();
      console.error = realErr;
      console.warn = realWarn;
    }
  });

  it('stop restores fetch, XHR and console, and payload goes back to null', () => {
    const origSend = FakeXhr.prototype.send;
    const origOpen = FakeXhr.prototype.open;
    startPageContextCapture();
    expect(window.fetch).not.toBe(fetchMock);
    expect(FakeXhr.prototype.send).not.toBe(origSend);
    stopPageContextCapture();
    expect(window.fetch).toBe(fetchMock);
    expect(FakeXhr.prototype.send).toBe(origSend);
    expect(FakeXhr.prototype.open).toBe(origOpen);
    expect(getPageContextPayload()).toBeNull();
  });
});
