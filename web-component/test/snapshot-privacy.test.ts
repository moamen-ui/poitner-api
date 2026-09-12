import { describe, it, expect, beforeEach } from 'vitest';
import {
  shallowSnapshot,
  escapeAttr,
  isMasked,
  isFormValueTag,
  isSensitiveAttr,
  maskAttrValue,
  captureMetadata,
} from '../src/capture';
import { generateSelector } from '../src/dom';

describe('snapshot-privacy', () => {
  beforeEach(() => {
    document.body.innerHTML = '';
    document.documentElement.removeAttribute('data-snapshot-mask');
    document.title = 'Test Page';
  });

  it('1. <input> with a typed value (set via the .value property, no attribute) -> value="•••"', () => {
    const input = document.createElement('input');
    input.type = 'text';
    input.value = 'secret typed value';
    document.body.appendChild(input);

    const snapshot = shallowSnapshot(input);
    expect(snapshot).toContain('value="•••"');
    expect(snapshot).not.toContain('secret typed value');
  });

  it('2. <input value="x"> with the property cleared -> attribute dropped', () => {
    const input = document.createElement('input');
    input.type = 'text';
    input.setAttribute('value', 'secretval');
    input.value = '';
    document.body.appendChild(input);

    const snapshot = shallowSnapshot(input);
    expect(snapshot).not.toContain('value=');
    expect(snapshot).not.toContain('secretval');
    expect(snapshot).toBe('<input type="text"/>');
  });

  it('3. attribute value containing " is emitted as &quot;', () => {
    const div = document.createElement('div');
    div.setAttribute('title', 'say "hello"');
    div.textContent = 'content';
    document.body.appendChild(div);

    const snapshot = shallowSnapshot(div);
    expect(snapshot).toContain('title="say &quot;hello&quot;"');
    expect(snapshot).not.toContain('say "hello"');
  });

  it('attribute value containing < is emitted as &lt;', () => {
    const div = document.createElement('div');
    div.setAttribute('title', 'a < b');
    document.body.appendChild(div);

    const snapshot = shallowSnapshot(div);
    expect(snapshot).toContain('title="a &lt; b"');
  });

  it('4. data-customer-name inside a masked subtree -> data-customer-name="•••"', () => {
    const container = document.createElement('div');
    container.setAttribute('data-snapshot-mask', '');
    const span = document.createElement('span');
    span.setAttribute('data-customer-name', 'Jane Doe');
    span.textContent = 'Jane Doe';
    container.appendChild(span);
    document.body.appendChild(container);

    const snapshot = shallowSnapshot(span);
    expect(snapshot).toContain('data-customer-name="•••"');
    expect(snapshot).not.toContain('Jane Doe');
    expect(snapshot).toBe('<span data-customer-name="•••">•••</span>');
  });

  it('5. data-username dropped everywhere', () => {
    const div = document.createElement('div');
    div.setAttribute('id', 'u1');
    div.setAttribute('data-username', 'johndoe');
    div.setAttribute('data-user-id', '12345');
    div.textContent = 'Profile';
    document.body.appendChild(div);

    const snapshot = shallowSnapshot(div);
    expect(snapshot).not.toContain('data-username');
    expect(snapshot).not.toContain('data-user-id');
    expect(snapshot).not.toContain('johndoe');
    expect(snapshot).toBe('<div id="u1">Profile</div>');
  });

  it('6. <textarea> text masked', () => {
    const textarea = document.createElement('textarea');
    textarea.value = 'confidential feedback';
    document.body.appendChild(textarea);

    const snapshot = shallowSnapshot(textarea);
    expect(snapshot).toBe('<textarea>•••</textarea>');
    expect(snapshot).not.toContain('confidential feedback');
  });

  it('7. <select> options not leaked', () => {
    const select = document.createElement('select');
    const opt1 = document.createElement('option');
    opt1.value = '1';
    opt1.textContent = 'Secret Option A';
    const opt2 = document.createElement('option');
    opt2.value = '2';
    opt2.textContent = 'Secret Option B';
    select.appendChild(opt1);
    select.appendChild(opt2);
    document.body.appendChild(select);

    const snapshot = shallowSnapshot(select);
    expect(snapshot).toBe('<select>•••</select>');
    expect(snapshot).not.toContain('Secret Option A');
    expect(snapshot).not.toContain('Secret Option B');
    expect(snapshot).not.toContain('value=');
  });

  it('<option> tag drops value and emits no text content', () => {
    const opt = document.createElement('option');
    opt.value = 'secret-val';
    opt.textContent = 'Secret Name';
    document.body.appendChild(opt);

    const snapshot = shallowSnapshot(opt);
    expect(snapshot).toBe('<option></option>');
    expect(snapshot).not.toContain('secret-val');
    expect(snapshot).not.toContain('Secret Name');
  });

  it('8. element inside <div data-snapshot-mask> -> text •••, structural attrs kept, data-email dropped', () => {
    const maskDiv = document.createElement('div');
    maskDiv.setAttribute('data-snapshot-mask', '');
    const button = document.createElement('button');
    button.id = 'submit-btn';
    button.type = 'submit';
    button.setAttribute('role', 'button');
    button.setAttribute('aria-label', 'Submit Form');
    button.setAttribute('data-email', 'alice@example.com');
    button.setAttribute('title', 'Click to submit');
    button.textContent = 'Submit';
    maskDiv.appendChild(button);
    document.body.appendChild(maskDiv);

    const snapshot = shallowSnapshot(button);
    expect(snapshot).toContain('id="submit-btn"');
    expect(snapshot).toContain('type="submit"');
    expect(snapshot).toContain('role="button"');
    expect(snapshot).toContain('aria-label="Submit Form"');
    expect(snapshot).toContain('title="•••"');
    expect(snapshot).not.toContain('data-email');
    expect(snapshot).not.toContain('alice@example.com');
    expect(snapshot).toContain('>•••</button>');
  });

  it('9. captureText=false -> no text for any element, attrs intact', () => {
    const button = document.createElement('button');
    button.id = 'cta';
    button.type = 'submit';
    button.textContent = 'Save Changes';
    document.body.appendChild(button);

    const snapshot = shallowSnapshot(button, false);
    expect(snapshot).toBe('<button id="cta" type="submit">•••</button>');
    expect(snapshot).not.toContain('Save Changes');

    const meta = captureMetadata(button, 'data-component-source', { captureText: false });
    expect(meta.snapshot).toBe('<button id="cta" type="submit">•••</button>');
    expect(meta.snapshot).not.toContain('Save Changes');
  });

  it('10. selector still generated for a masked element', () => {
    const table = document.createElement('table');
    table.setAttribute('data-snapshot-mask', '');
    table.id = 'customers-table';
    const tbody = document.createElement('tbody');
    const tr = document.createElement('tr');
    const td = document.createElement('td');
    td.id = 'target-cell';
    td.textContent = 'Private customer info';
    tr.appendChild(td);
    tbody.appendChild(tr);
    table.appendChild(tbody);
    document.body.appendChild(table);

    expect(isMasked(td)).toBe(true);
    const selector = generateSelector(td);
    expect(selector).toBe('#target-cell');

    const meta = captureMetadata(td, 'data-component-source');
    expect(meta.selector).toBe('#target-cell');
    expect(meta.snapshot).toBe('<td id="target-cell">•••</td>');
  });

  it('11. unmasked <button>Save</button> unchanged (regression)', () => {
    const button = document.createElement('button');
    button.textContent = 'Save';
    document.body.appendChild(button);

    const snapshot = shallowSnapshot(button);
    expect(snapshot).toBe('<button>Save</button>');
  });

  it('sensitive attributes list: data-value, data-token, data-secret, authorization, srcdoc are dropped', () => {
    const div = document.createElement('div');
    div.setAttribute('id', 'd1');
    div.setAttribute('data-value', 'val1');
    div.setAttribute('data-token', 'tok1');
    div.setAttribute('data-secret', 'sec1');
    div.setAttribute('authorization', 'Bearer 123');
    div.setAttribute('srcdoc', '<html>');
    div.textContent = 'Hello';
    document.body.appendChild(div);

    const snapshot = shallowSnapshot(div);
    expect(snapshot).toBe('<div id="d1">Hello</div>');
  });
});
