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
  // Handle string names as well 1=Text, 2=Url, 3=Select
  const isText = type === 1 || type === 'Text';
  const isUrl = type === 2 || type === 'Url';
  const isSelect = type === 3 || type === 'Select';

  if (isText) {
    if (value.length > 500) return 'fields.tooLong';
  } else if (isUrl) {
    if (value.length > 2000) return 'fields.tooLong';
    let url: URL;
    try {
      url = new URL(value);
    } catch {
      return 'fields.invalidUrl';
    }
    if (url.protocol !== 'http:' && url.protocol !== 'https:') {
      return 'fields.invalidUrl';
    }
    if (url.username || url.password) {
      return 'fields.invalidUrl';
    }
    if (!url.hostname) {
      return 'fields.invalidUrl';
    }
    
    if (def.allowedHosts && def.allowedHosts.length > 0) {
      const match = def.allowedHosts.some(h => hostMatches(url.hostname, h));
      if (!match) return 'fields.hostNotAllowed';
    }
  } else if (isSelect) {
    if (def.options && !def.options.includes(value)) {
      return 'fields.invalidOption';
    }
  }
  return null;
}

export function renderFieldInputs(defs: CommentFieldDefinition[], values: Record<string, string>, idPrefix: string): string {
  if (!defs || defs.length === 0) return '';
  return defs.map(def => {

    const isUrl = def.type === 2 || def.type === 'Url';
    const isSelect = def.type === 3 || def.type === 'Select';
    const id = escapeHtml(`${idPrefix}-${def.key}`);
    const name = escapeHtml(`fbk-cf-${def.key}`);
    const val = values[def.key] || '';
    
    let inputHtml = '';
    if (isSelect) {
      inputHtml = `<select id="${id}" name="${name}" class="fbk-input">
        <option value="">${escapeHtml(t('fields.none'))}</option>
        ${(def.options || []).map(o => `<option value="${escapeHtml(o)}" ${o === val ? 'selected' : ''}>${escapeHtml(o)}</option>`).join('')}
      </select>`;
    } else {
      const typeAttr = isUrl ? 'url' : 'text';
      const inputMode = isUrl ? ' inputmode="url"' : '';
      const maxLength = isUrl ? ' maxlength="2000"' : ' maxlength="500"';
      inputHtml = `<input type="${typeAttr}" id="${id}" name="${name}" class="fbk-input" value="${escapeHtml(val)}"${inputMode}${maxLength}>`;
    }
    
    let hintHtml = '';
    if (def.hint) {
      hintHtml = `<small class="fbk-field-hint" id="${id}-hint">${escapeHtml(def.hint)}</small>`;
    }
    
    return `
      <div class="fbk-field">
        <label for="${id}">${escapeHtml(def.label)}</label>
        ${inputHtml}
        ${hintHtml}
      </div>
    `;
  }).join('');
}

export function collectFieldValues(root: HTMLElement): Record<string, string> {
  const map: Record<string, string> = {};
  const inputs = root.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[name^="fbk-cf-"]');
  for (let i = 0; i < inputs.length; i++) {
    const el = inputs[i];
    const key = el.name.substring(7); // remove "fbk-cf-"
    const val = el.value.trim();
    if (val) {
      map[key] = val;
    }
  }
  return map;
}
