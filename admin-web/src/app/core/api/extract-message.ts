export function extractMessage(error: unknown): string {
  if (error && typeof error === 'object') {
    const e = error as Record<string, unknown>;
    const inner = e['error'];
    if (inner && typeof inner === 'object') {
      const innerMsg = (inner as Record<string, unknown>)['message'];
      if (innerMsg) return String(innerMsg);
    }
    const msg = e['message'];
    if (msg) return String(msg);
  }
  return 'Request failed';
}
