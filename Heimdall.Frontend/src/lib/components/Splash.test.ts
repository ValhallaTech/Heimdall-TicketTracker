/**
 * Unit spec for the Splash landing component.
 *
 * Complements the route smoke spec (page.smoke.test.ts) by exercising the
 * component's prop contract directly: the default `title`/`tagline` runes and
 * their overrides. Asserting overrides here proves the `$props()` bindings are
 * actually rendered (not hard-coded), covering the component's branches.
 */
import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/svelte';

import Splash from './Splash.svelte';

describe('Splash', () => {
  it('renders the default title and tagline', () => {
    render(Splash);

    expect(screen.getByTestId('splash')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1, name: 'Heimdall' })).toBeInTheDocument();
    expect(screen.getByText('Ticket tracking, guarded.')).toBeInTheDocument();
  });

  it('renders an overridden title prop', () => {
    render(Splash, { props: { title: 'Bifrost' } });

    expect(screen.getByRole('heading', { level: 1, name: 'Bifrost' })).toBeInTheDocument();
  });

  it('renders an overridden tagline prop', () => {
    render(Splash, { props: { tagline: 'Guarding the gates.' } });

    expect(screen.getByText('Guarding the gates.')).toBeInTheDocument();
  });

  it('renders both overridden props together', () => {
    render(Splash, { props: { title: 'Asgard', tagline: 'All seeing.' } });

    expect(screen.getByRole('heading', { level: 1, name: 'Asgard' })).toBeInTheDocument();
    expect(screen.getByText('All seeing.')).toBeInTheDocument();
  });
});
