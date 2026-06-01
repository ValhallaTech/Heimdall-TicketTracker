import { clsx, type ClassValue } from 'clsx';
import { twMerge } from 'tailwind-merge';

/**
 * Merge Tailwind class lists with conflict resolution.
 *
 * Standard shadcn-svelte helper: `clsx` flattens conditional class inputs and
 * `tailwind-merge` de-duplicates conflicting Tailwind utilities (last wins).
 */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
