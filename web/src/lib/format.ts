/**
 * Central formatting. DESIGN.md's quality floor: all money and dates are formatted here, never
 * ad hoc in a component, and timestamps render in the viewer's own locale and time zone.
 */

const CURRENCY = 'USD';
const LOCALE = undefined; // the browser's locale

const money = new Intl.NumberFormat(LOCALE, {
  style: 'currency',
  currency: CURRENCY,
  minimumFractionDigits: 2,
  maximumFractionDigits: 2,
});

const compactFormatters = new Map<number | undefined, Intl.NumberFormat>();

function compactFormatter(decimals: number | undefined): Intl.NumberFormat {
  let formatter = compactFormatters.get(decimals);
  if (!formatter) {
    formatter = new Intl.NumberFormat(LOCALE, {
      style: 'currency',
      currency: CURRENCY,
      notation: 'compact',
      minimumFractionDigits: decimals,
      maximumFractionDigits: decimals ?? 1,
    });
    compactFormatters.set(decimals, formatter);
  }
  return formatter;
}

const count = new Intl.NumberFormat(LOCALE);

const percentFormatters = new Map<number, Intl.NumberFormat>();

function percentFormatter(digits: number): Intl.NumberFormat {
  let formatter = percentFormatters.get(digits);
  if (!formatter) {
    formatter = new Intl.NumberFormat(LOCALE, {
      style: 'percent',
      minimumFractionDigits: digits,
      maximumFractionDigits: digits,
    });
    percentFormatters.set(digits, formatter);
  }
  return formatter;
}

const dateOnly = new Intl.DateTimeFormat(LOCALE, { dateStyle: 'medium' });
const dateAndTime = new Intl.DateTimeFormat(LOCALE, { dateStyle: 'medium', timeStyle: 'short' });
const timeOnly = new Intl.DateTimeFormat(LOCALE, { timeStyle: 'short' });
const relative = new Intl.RelativeTimeFormat(LOCALE, { numeric: 'auto' });

/** `$24,800,000.00` — thousands separators and a currency prefix, per DESIGN.md. */
export function formatMoney(value: number): string {
  return money.format(value);
}

/**
 * `$24.8M` — the KPI-card form. Pass `decimals` to force them, so a row of figures lines up as
 * `$18.6M / $21.0M / -$2.4M` rather than dropping the one whose decimal happens to be zero.
 */
export function formatMoneyCompact(value: number, decimals?: number): string {
  return compactFormatter(decimals).format(value);
}

/** `1,274` */
export function formatCount(value: number): string {
  return count.format(value);
}

/** Takes a fraction (0.064), renders `6.4%`. One decimal by default; pass 0 for a whole number. */
export function formatPercent(fraction: number, digits = 1): string {
  return percentFormatter(digits).format(fraction);
}

export function formatDate(value: Date | string): string {
  return dateOnly.format(toDate(value));
}

export function formatDateTime(value: Date | string): string {
  return dateAndTime.format(toDate(value));
}

export function formatTime(value: Date | string): string {
  return timeOnly.format(toDate(value));
}

const MINUTE = 60_000;
const HOUR = 60 * MINUTE;
const DAY = 24 * HOUR;

/** `10m ago`, `3h ago`, `2d ago` — the alert-list form from the reference dashboard. */
export function formatRelativeTime(value: Date | string, now: Date = new Date()): string {
  const elapsed = toDate(value).getTime() - now.getTime();
  const magnitude = Math.abs(elapsed);

  if (magnitude < MINUTE) return relative.format(0, 'second').replace('now', 'just now');
  if (magnitude < HOUR) return compact(Math.round(elapsed / MINUTE), 'm');
  if (magnitude < DAY) return compact(Math.round(elapsed / HOUR), 'h');
  return compact(Math.round(elapsed / DAY), 'd');
}

function compact(value: number, unit: string): string {
  return value < 0 ? `${Math.abs(value)}${unit} ago` : `in ${value}${unit}`;
}

function toDate(value: Date | string): Date {
  return value instanceof Date ? value : new Date(value);
}

/**
 * Round axis ticks from 0 to a value at or above `max`, so a chart reads $0/$1M/$2M/$3M rather
 * than the raw data's maximum. Returns `count` + 1 values including both ends.
 */
export function niceTicks(max: number, count = 3): number[] {
  if (!Number.isFinite(max) || max <= 0) return [0];

  const rawStep = max / count;
  const magnitude = 10 ** Math.floor(Math.log10(rawStep));
  const step =
    [1, 2, 2.5, 4, 5, 8, 10].map((m) => m * magnitude).find((candidate) => candidate >= rawStep) ??
    magnitude * 10;

  return Array.from({ length: count + 1 }, (_, index) => index * step);
}
