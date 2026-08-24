import { Search, X } from 'lucide-react';
import { useId } from 'react';
import type * as React from 'react';
import { Input } from '@/components/ui/input';
import { Select } from '@/components/ui/select';
import { cn } from '@/lib/utils';

/**
 * The filter row above a registry table. Everything in here maps to a query parameter the list
 * endpoint understands, so narrowing a registry narrows the request rather than the rendered array.
 */

export function FilterBar({ children, className }: { children: React.ReactNode; className?: string }) {
  return (
    <div className={cn('flex flex-wrap items-center gap-2', className)}>{children}</div>
  );
}

export function SearchField({
  value,
  onChange,
  placeholder,
  label,
  className,
}: {
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  /** Names the box for a screen reader — "Search customers". */
  label: string;
  className?: string;
}) {
  return (
    <div className={cn('relative min-w-0 flex-1 sm:max-w-xs', className)}>
      <Search
        className="text-muted pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2"
        strokeWidth={1.75}
        aria-hidden="true"
      />
      <Input
        type="search"
        value={value}
        aria-label={label}
        placeholder={placeholder}
        onChange={(event) => onChange(event.target.value)}
        className="h-9 pr-8 pl-9 text-[13px]"
      />
      {value && (
        <button
          type="button"
          aria-label="Clear search"
          onClick={() => onChange('')}
          className="text-muted hover:text-heading rounded-field absolute top-1/2 right-2 -translate-y-1/2 p-0.5 transition-colors"
        >
          <X className="size-3.5" strokeWidth={2} aria-hidden="true" />
        </button>
      )}
    </div>
  );
}

/**
 * A filter select whose empty option means "no filter". The value is the API's own enum name, so
 * nothing has to translate a label back into something the host will parse; `format` is only how it
 * reads on screen.
 */
/** Stable reference: a default arrow expression would be a new function on every render. */
const identity = (option: string) => option;

export function FilterSelect<TValue extends string>({
  label,
  value,
  onChange,
  options,
  anyLabel,
  format = identity,
}: {
  label: string;
  value: TValue | '';
  onChange: (value: TValue | '') => void;
  options: readonly TValue[];
  /** The empty option's text — "All statuses". */
  anyLabel: string;
  format?: (option: TValue) => string;
}) {
  return (
    <Select
      aria-label={label}
      value={value}
      onChange={(event) => onChange(event.target.value as TValue | '')}
      className={cn(value === '' && 'text-body font-normal')}
    >
      <option value="">{anyLabel}</option>
      {options.map((option) => (
        <option key={option} value={option}>
          {format(option)}
        </option>
      ))}
    </Select>
  );
}

/** A checkbox rendered as a pill — "Low stock only", "Include discontinued". */
export function FilterToggle({
  label,
  checked,
  onChange,
}: {
  label: string;
  checked: boolean;
  onChange: (checked: boolean) => void;
}) {
  const id = useId();

  return (
    <label
      htmlFor={id}
      className={cn(
        'rounded-control flex h-9 cursor-pointer items-center gap-2 border px-3 text-[13px] font-medium transition-colors',
        checked
          ? 'border-primary bg-primary-soft text-primary'
          : 'border-border bg-card text-body hover:bg-canvas',
      )}
    >
      <input
        id={id}
        type="checkbox"
        checked={checked}
        onChange={(event) => onChange(event.target.checked)}
        className="accent-primary size-3.5"
      />
      {label}
    </label>
  );
}

/** Clears every filter at once. Hidden when there is nothing to clear. */
export function ClearFilters({ show, onClear }: { show: boolean; onClear: () => void }) {
  if (!show) return null;

  return (
    <button
      type="button"
      onClick={onClear}
      className="text-muted hover:text-heading rounded-control h-9 px-2 text-[13px] font-medium transition-colors"
    >
      Clear filters
    </button>
  );
}
