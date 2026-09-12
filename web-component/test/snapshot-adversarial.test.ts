import { describe, test, expect } from 'vitest';
import { shallowSnapshot } from '../src/capture';

/**
 * Adversarial pass over the snapshot sanitizer.
 *
 * The delivered tests cover the documented rules. These ask the only question that actually
 * matters for a privacy guarantee: given real secrets typed into a real form, can ANY of them
 * reach the snapshot by any route?
 */
function form(): HTMLElement {
  const host = document.createElement('div');
  host.innerHTML = `
    <form id="probe">
      <input type="password" name="pw" />
      <input type="text" name="email" />
      <input type="hidden" name="csrf" value="HIDDEN-TOKEN-VALUE" />
      <textarea name="notes"></textarea>
      <select name="plan"><option value="PLAN-VALUE">PLAN-LABEL</option></select>
      <div data-snapshot-mask>
        <span>MASKED-SPAN-TEXT</span>
        <input name="inner" />
      </div>
    </form>`;
  document.body.appendChild(host);

  (host.querySelector('input[type=password]') as HTMLInputElement).value = 'REAL-PASSWORD';
  (host.querySelector('input[name=email]') as HTMLInputElement).value = 'alice@private.test';
  (host.querySelector('textarea') as HTMLTextAreaElement).value = 'NOTES-SECRET';
  (host.querySelector('input[name=inner]') as HTMLInputElement).value = 'INNER-SECRET';

  return host.querySelector('#probe') as HTMLElement;
}

const SECRETS = [
  'REAL-PASSWORD',
  'alice@private.test',
  'NOTES-SECRET',
  'INNER-SECRET',
  'HIDDEN-TOKEN-VALUE',
];

describe('no typed value reaches the snapshot by any route', () => {
  test('with text capture ON', () => {
    const snap = shallowSnapshot(form(), true);
    for (const secret of SECRETS) {
      expect(snap, `snapshot leaked ${secret}`).not.toContain(secret);
    }
  });

  test('with text capture OFF', () => {
    const snap = shallowSnapshot(form(), false);
    for (const secret of SECRETS) {
      expect(snap, `snapshot leaked ${secret}`).not.toContain(secret);
    }
  });

  test('a filled field is still shown as filled, so the snapshot stays useful', () => {
    // Dropping the value is right; hiding THAT there was one would make a bug report about a
    // filled-in form unreadable. shallowSnapshot renders the element it is GIVEN, so this is
    // asserted on the input itself rather than on an ancestor form.
    const host = document.createElement('div');
    host.innerHTML = '<input type="password" name="pw" />';
    document.body.appendChild(host);
    const input = host.firstElementChild as HTMLInputElement;
    input.value = 'REAL-PASSWORD';

    const snap = shallowSnapshot(input, true);
    expect(snap).toContain('•••');
    expect(snap).not.toContain('REAL-PASSWORD');
  });

  test('data-snapshot-mask hides descendant text', () => {
    const host = document.createElement('div');
    host.innerHTML = '<div data-snapshot-mask><span>MASKED-SPAN-TEXT</span></div>';
    document.body.appendChild(host);

    const snap = shallowSnapshot(host.firstElementChild as HTMLElement, true);
    expect(snap).not.toContain('MASKED-SPAN-TEXT');
  });

  test('structure survives — this is a debugging aid, not a redaction', () => {
    const snap = shallowSnapshot(form(), true);
    expect(snap).toContain('form');
    expect(snap).toContain('probe');
  });
});
