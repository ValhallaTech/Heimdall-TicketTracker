/**
 * Unit spec for the root layout (`+layout.svelte`).
 *
 * The layout imports global styles and the favicon, then renders its `children`
 * snippet. We supply a raw snippet so the layout's `{@render children()}` path
 * executes and we can assert the slotted content appears in the document.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';
import { createRawSnippet } from 'svelte';

import Layout from './+layout.svelte';

describe('+layout', () => {
  it('renders its children snippet', () => {
    const children = createRawSnippet(() => ({
      render: () => `<div data-testid="layout-child">routed content</div>`,
    }));

    render(Layout, { props: { children } });

    expect(screen.getByTestId('layout-child')).toBeInTheDocument();
    expect(screen.getByText('routed content')).toBeInTheDocument();
  });
});
