/**
 * Unit spec for the `cn()` Tailwind class-merge helper.
 *
 * `cn()` composes clsx (conditional flattening) with tailwind-merge (conflict
 * resolution). These cases lock in the behaviour the rest of the UI relies on:
 * joining classes, dropping falsy values, and last-wins conflict resolution.
 */
import { describe, expect, it } from 'vitest';

import { cn } from './utils';

describe('cn', () => {
  it('joins multiple class names', () => {
    expect(cn('px-2', 'py-1')).toBe('px-2 py-1');
  });

  it('ignores falsy and conditional values', () => {
    expect(cn('px-2', false, null, undefined, 'py-1')).toBe('px-2 py-1');
  });

  it('flattens array and object inputs', () => {
    expect(cn(['px-2', 'py-1'], { 'text-sm': true, hidden: false })).toBe('px-2 py-1 text-sm');
  });

  it('resolves conflicting Tailwind utilities with last-wins', () => {
    expect(cn('px-2', 'px-4')).toBe('px-4');
  });

  it('returns an empty string when given no meaningful input', () => {
    expect(cn()).toBe('');
    expect(cn(false, null, undefined)).toBe('');
  });
});
