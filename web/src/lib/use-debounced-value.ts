import { useEffect, useState } from 'react';

/**
 * Trails a fast-changing value so it can be used in a query key. The registry searches run on the
 * server, so without this every keystroke would be its own request — and the answers could land out
 * of order, leaving the table showing the results for a prefix of what is in the box.
 */
export function useDebouncedValue<T>(value: T, delayMs = 300): T {
  const [debounced, setDebounced] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);

  // Derived rather than pushed from the effect: an empty box is not a search, so clearing the field
  // shows the whole registry again on the next render instead of after the delay.
  return value === '' ? value : debounced;
}
