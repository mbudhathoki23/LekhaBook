// The Back button of a phone closes what is open on top before it changes screen.
// MudBlazor puts a .mud-overlay behind dialogs, message boxes, drop-down lists, menus and date pickers;
// clicking it is the same as tapping outside.

function isShown(el) {
    return el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden';
}

function layer(el) {
    for (let node = el; node instanceof Element; node = node.parentElement) {
        const z = Number.parseInt(getComputedStyle(node).zIndex, 10);
        if (!Number.isNaN(z)) {
            return z;
        }
    }
    return 0;
}

/** Closes the topmost popup. Returns true when one was open, even if it refused to close. */
export function closeTopPopup() {
    const overlays = [...document.querySelectorAll('.mud-overlay')].filter(isShown);
    if (overlays.length > 0) {
        // Highest layer wins; between equals, the later element is on top.
        overlays.reduce((top, next) => (layer(next) >= layer(top) ? next : top)).click();
        return true;
    }

    // A list without a backdrop closes when its field gets Escape or loses focus.
    if ([...document.querySelectorAll('.mud-popover-open:not(.mud-tooltip)')].some(isShown)) {
        const field = document.activeElement;
        if (field instanceof HTMLElement) {
            field.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true }));
            field.blur();
        }
        return true;
    }

    return false;
}
