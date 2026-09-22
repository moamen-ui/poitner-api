import type { CommentFieldDefinition } from './types';
import { escapeHtml } from './dom';
import { t } from './i18n';

export function hostMatches(host: string, pattern: string): boolean {
  host = host.toLowerCase();
  pattern = pattern.toLowerCase();
  if (host === pattern) return true;
  if (pattern.startsWith('*.')) {
    const domain = pattern.substring(2);
    if (host === domain || host.endsWith('.' + domain)) {
      return true;
    }
  }
  return false;
}

export function validateFieldValue(def: CommentFieldDefinition, value: string): string | null {
  value = value.trim();
  if (!value) return null;

  const type = def.type;
  const isText = type === 1 || type === 'Text';
  const isUrl = type === 2 || type === 'Url';
  const isSelect = type === 3 || type === 'Select';

  if (isText) {
    if (value.length > 500) return 'fields.tooLong';
  } else if (isUrl) {
    if (value.length > 2000) return 'fields.tooLong';
    try {
      const u = new URL(value);
      if (u.protocol !== 'http:' && u.protocol !== 'https:') return 'fields.invalidUrl';
      if (u.username || u.password || !u.hostname) return 'fields.invalidUrl';
      if (def.allowedHosts && def.allowedHosts.length > 0 && !def.allowedHosts.some((h) => hostMatches(u.hostname, h))) return 'fields.hostNotAllowed';
    } catch {
      return 'fields.invalidUrl';
    }
  } else if (isSelect) {
    if (def.options && !def.options.includes(value)) return 'fields.invalidOption';
  }
  return null;
}

export function renderFieldInputs(defs: CommentFieldDefinition[], values: Record<string, string>, idPrefix: string): string {
  if (!defs || defs.length === 0) return '';
  return defs.map((def) => {
    const isUrl = def.type === 2 || def.type === 'Url';
    const isSelect = def.type === 3 || def.type === 'Select';
    const id = escapeHtml(`${idPrefix}-${def.key}`);
    const name = escapeHtml(`fbk-cf-${def.key}`);
    const val = values[def.key] || '';
    let inputHtml = '';
    if (isSelect) {
      inputHtml = `<select id="${id}" name="${name}" class="fbk-input"><option value="">${escapeHtml(t('fields.none'))}</option>${(def.options || []).map((o) => `<option value="${escapeHtml(o)}"${o === val ? ' selected' : ''}>${escapeHtml(o)}</option>`).join('')}</select>`;
    } else {
      inputHtml = `<input type="${isUrl ? 'url' : 'text'}" id="${id}" name="${name}" class="fbk-input" value="${escapeHtml(val)}"${isUrl ? ' inputmode="url" maxlength="2000"' : ' maxlength="500"'}>`;
    }
    const hintHtml = def.hint ? `<small class="fbk-field-hint" id="${id}-hint">${escapeHtml(def.hint)}</small>` : '';
    return `<div class="fbk-field"><label for="${id}">${escapeHtml(def.label)}</label>${inputHtml}${hintHtml}</div>`;
  }).join('');
}

export function collectFieldValues(root: HTMLElement): Record<string, string> {
  const map: Record<string, string> = {};
  root.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[name^="fbk-cf-"]').forEach((el) => {
    const val = el.value.trim();
    if (val) map[el.name.substring(7)] = val;
  });
  return map;
}
