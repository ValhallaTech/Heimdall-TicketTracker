import adapter from '@sveltejs/adapter-node';

/** @type {import('@sveltejs/kit').Config} */
const config = {
  compilerOptions: {
    // Force runes mode for the project, except for libraries. Can be removed in svelte 6.
    runes: ({ filename }) => (filename.split(/[/\\]/).includes('node_modules') ? undefined : true),
  },
  kit: {
    // adapter-node produces a standalone Node server for SSR + form actions,
    // per blazor-to-svelte-transition.md §3.3 (Topology B, portable deployment).
    adapter: adapter(),
  },
};

export default config;
