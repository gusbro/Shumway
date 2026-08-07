// Light and dark, without a second set of colours.
//
// The stylesheet is written in `light-dark()` under `color-scheme: light dark`,
// which means the browser already knows both palettes and picks by scheme. So
// choosing a theme is just narrowing that scheme on the root element: every
// `light-dark()` in the sheet follows, and there is nothing to keep in step.
//
// Three states, one button: with no choice stored the page follows the
// operating system, and the first click fixes the opposite of whatever is on
// screen. After that it is explicit, which is what someone who reached for the
// button was asking for.

const SUN = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" fill="none" '
  + 'stroke="currentColor" stroke-width="2" stroke-linecap="round">'
  + '<circle cx="12" cy="12" r="4.2" /><path d="M12 2v2.4M12 19.6V22M2 12h2.4M19.6 12H22'
  + 'M4.9 4.9l1.7 1.7M17.4 17.4l1.7 1.7M19.1 4.9l-1.7 1.7M6.6 17.4l-1.7 1.7" /></svg>';

const MOON = '<svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true" fill="none" '
  + 'stroke="currentColor" stroke-width="2" stroke-linejoin="round" stroke-linecap="round">'
  + '<path d="M20 14.2A8.2 8.2 0 0 1 9.8 4a8.4 8.4 0 1 0 10.2 10.2z" /></svg>';

const systemPrefersDark = () =>
  window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches;

/** What is actually on screen, given a stored choice of 'light' | 'dark' | null. */
export const effective = (choice) => choice ?? (systemPrefersDark() ? 'dark' : 'light');

/**
 * Attaches the toggle.
 * @param {HTMLButtonElement} button
 * @param {'light'|'dark'|null} choice the stored preference
 * @param {(choice: 'light'|'dark') => void} onChange asked to remember a new one
 */
export function attach(button, choice, onChange) {
  const root = document.documentElement;
  const themeColor = document.querySelector('meta[name="theme-color"]');

  function apply(current) {
    const now = effective(current);
    // Narrowing the scheme is the whole mechanism; an empty value hands the
    // decision back to the operating system.
    root.style.colorScheme = current ?? '';
    root.dataset.theme = now;
    // The browser paints its own chrome from this, so it has to agree.
    if (themeColor) themeColor.content = now === 'dark' ? '#111111' : '#fbfbfb';
    // The label says what the button DOES, not what is on screen — that is what
    // a screen reader needs to hear, and what a tooltip should say.
    const next = now === 'dark' ? 'light' : 'dark';
    button.innerHTML = now === 'dark' ? SUN : MOON;
    button.title = `Switch to ${next} theme`;
    button.setAttribute('aria-label', button.title);
  }

  apply(choice);

  button.addEventListener('click', () => {
    choice = effective(choice) === 'dark' ? 'light' : 'dark';
    apply(choice);
    onChange(choice);
  });

  // While the page is still following the system, follow it as it changes.
  window.matchMedia?.('(prefers-color-scheme: dark)')
    .addEventListener?.('change', () => { if (choice === null) apply(null); });
}
