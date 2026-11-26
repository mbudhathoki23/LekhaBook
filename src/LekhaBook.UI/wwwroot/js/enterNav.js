// Enter-key navigation for forms, like a Windows accounting app (AGENT_PLAN § 8.3).
// Runs the same in the browser and inside the MAUI BlazorWebView.
//
// - Enter moves to the next field, Shift+Enter to the previous one (fields in DOM order).
// - Enter on the last field, or Ctrl+S anywhere, presses the button marked [data-enter-save].
// - Esc presses the button marked [data-enter-cancel].
// - Textareas keep Enter for new lines; Ctrl+Enter moves on.
// - An open dropdown or autocomplete list (aria-expanded="true") keeps Enter to pick an item.
// - A radio group is one stop; arrow keys change the choice.
// - Phone keyboards show "Next" on every field and "Done" on the last (enterkeyhint).
// - Amount fields (inputmode="decimal") select their text on focus, so typing replaces it.

const FIELD_SELECTOR = [
    'input:not([type=hidden]):not([type=button]):not([type=submit]):not([type=reset]):not([type=file]):not([type=image])',
    'select',
    'textarea',
].join(',');

const attached = new WeakMap();
const movedByEnter = new WeakSet();

function isVisible(el) {
    if (!(el.offsetWidth || el.offsetHeight || el.getClientRects().length)) {
        return false;
    }
    return getComputedStyle(el).visibility !== 'hidden';
}

/** Fields Enter can land on, in DOM order, with each radio group collapsed to one stop. */
function fieldsOf(root) {
    const result = [];
    const radioGroups = new Set();
    for (const el of root.querySelectorAll(FIELD_SELECTOR)) {
        if (el.disabled || el.readOnly || el.closest('[data-enter-skip]') || !isVisible(el)) {
            continue;
        }
        if (el.type === 'radio') {
            if (!el.name || radioGroups.has(el.name)) {
                continue;
            }
            radioGroups.add(el.name);
            const group = [...root.querySelectorAll('input[type=radio]')].filter(r => r.name === el.name && !r.disabled);
            result.push(group.find(r => r.checked) ?? el);
            continue;
        }
        result.push(el);
    }
    return result;
}

function indexOf(fields, target) {
    return fields.findIndex(f => f === target || (target.type === 'radio' && f.type === 'radio' && f.name === target.name));
}

/** True while a dropdown/autocomplete list belonging to this field is open. */
function listIsOpen(target) {
    if (target.getAttribute('aria-expanded') === 'true') {
        return true;
    }
    const combo = target.closest('[role=combobox], .mud-autocomplete, .mud-select');
    return !!combo && (combo.getAttribute('aria-expanded') === 'true' || !!combo.querySelector('[aria-expanded="true"]'));
}

function focusField(el) {
    el.focus();
    el.scrollIntoView({ block: 'nearest' });
}

function press(root, selector) {
    const button = root.querySelector(selector);
    if (!button || button.disabled) {
        return false;
    }
    // Blur first so the field being edited sends its value before the click is handled.
    if (document.activeElement instanceof HTMLElement && root.contains(document.activeElement)) {
        document.activeElement.blur();
    }
    button.click();
    return true;
}

function updateHints(root) {
    const fields = fieldsOf(root);
    fields.forEach((f, i) => f.setAttribute('enterkeyhint', i === fields.length - 1 ? 'done' : 'next'));
}

function onKeyDown(e) {
    const root = e.currentTarget;
    const target = e.target;
    if (!(target instanceof HTMLElement) || e.isComposing) {
        return;
    }

    if (e.key === 'Enter') {
        movedByEnter.delete(root); // a new press: forget any release that never arrived
    }

    if (e.key === 'Escape') {
        if (!listIsOpen(target) && press(root, '[data-enter-cancel]')) {
            e.preventDefault();
        }
        return;
    }

    if ((e.ctrlKey || e.metaKey) && !e.altKey && (e.key === 's' || e.key === 'S')) {
        e.preventDefault();
        e.stopPropagation();
        press(root, '[data-enter-save]');
        return;
    }

    if (e.key !== 'Enter' || e.altKey) {
        return;
    }

    // Buttons and links keep Enter for clicking; open lists keep it for picking an item.
    if (target.matches('button, a, [role=button], [role=option]') || listIsOpen(target)) {
        return;
    }
    if (target.tagName === 'TEXTAREA' && !e.ctrlKey) {
        return;
    }

    const fields = fieldsOf(root);
    const i = indexOf(fields, target);
    if (i < 0) {
        return;
    }

    // Stop here (capture phase), so the field's own Enter handling doesn't also run.
    e.preventDefault();
    e.stopPropagation();
    // The key is released on the field we move to; keep that release from acting there (see onKeyUp).
    movedByEnter.add(root);

    if (e.shiftKey) {
        if (i > 0) {
            focusField(fields[i - 1]);
        }
    } else if (i === fields.length - 1) {
        press(root, '[data-enter-save]');
    } else {
        focusField(fields[i + 1]);
    }
}

/**
 * Swallows the release of an Enter that moved focus. Without this, a pre-filled autocomplete receives the
 * key-up, opens its list, and the next Enter only closes it instead of moving on.
 */
function onKeyUp(e) {
    if (e.key === 'Enter' && movedByEnter.delete(e.currentTarget)) {
        e.preventDefault();
        e.stopPropagation();
    }
}

function onFocusIn(e) {
    const root = e.currentTarget;
    const target = e.target;
    updateHints(root);
    if (target instanceof HTMLInputElement && target.inputMode === 'decimal') {
        // After the browser places the caret, select everything so typing replaces the amount.
        setTimeout(() => {
            if (document.activeElement === target) {
                target.select();
            }
        }, 0);
    }
}

/**
 * Starts Enter navigation inside root. With focusFirst, focuses the first field on devices with a
 * mouse (on phones that would pop up the keyboard unasked).
 */
export function attach(root, focusFirst) {
    if (!root || attached.has(root)) {
        return;
    }
    root.addEventListener('keydown', onKeyDown, true);
    root.addEventListener('keyup', onKeyUp, true);
    root.addEventListener('focusin', onFocusIn);
    attached.set(root, true);
    updateHints(root);
    if (focusFirst && window.matchMedia('(pointer: fine)').matches) {
        focusFirstField(root);
    }
}

export function focusFirstField(root) {
    const first = root && fieldsOf(root)[0];
    if (first) {
        focusField(first);
    }
}

/** Focuses the first field showing a validation error, so the user can fix it straight away. */
export function focusFirstError(root) {
    const invalid = root && [...root.querySelectorAll('[aria-invalid="true"]')].find(el => fieldsOf(root).includes(el));
    if (invalid) {
        focusField(invalid);
    }
}

export function detach(root) {
    if (!root || !attached.has(root)) {
        return;
    }
    root.removeEventListener('keydown', onKeyDown, true);
    root.removeEventListener('keyup', onKeyUp, true);
    root.removeEventListener('focusin', onFocusIn);
    attached.delete(root);
}
