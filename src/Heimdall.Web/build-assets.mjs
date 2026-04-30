// Copies Bootstrap 5.3 CSS + JS bundle and Font Awesome 7 assets from node_modules into wwwroot/.
// Invoked by `yarn build` and from the Dockerfile build stage.
import { mkdirSync, copyFileSync, readdirSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const from = (...p) => resolve(here, 'node_modules', ...p);
const to = (...p) => resolve(here, 'wwwroot', ...p);

const targets = [
  [from('bootstrap', 'dist', 'css', 'bootstrap.min.css'), to('css', 'bootstrap.min.css')],
  [from('bootstrap', 'dist', 'css', 'bootstrap.min.css.map'), to('css', 'bootstrap.min.css.map')],
  [from('bootstrap', 'dist', 'js', 'bootstrap.bundle.min.js'), to('js', 'bootstrap.bundle.min.js')],
  [from('bootstrap', 'dist', 'js', 'bootstrap.bundle.min.js.map'), to('js', 'bootstrap.bundle.min.js.map')],
  // Font Awesome 7 — CSS references ../webfonts/ relative paths, so keep that layout
  [from('@fortawesome', 'fontawesome-free', 'css', 'all.min.css'), to('css', 'fontawesome.all.min.css')],
];

for (const [src, dest] of targets) {
  mkdirSync(dirname(dest), { recursive: true });
  copyFileSync(src, dest);
  console.log(`copied ${src} -> ${dest}`);
}

// Copy all Font Awesome webfont files into wwwroot/webfonts/
const webfontsDir = from('@fortawesome', 'fontawesome-free', 'webfonts');
const webfontsDest = to('webfonts');
mkdirSync(webfontsDest, { recursive: true });
for (const file of readdirSync(webfontsDir)) {
  const src = resolve(webfontsDir, file);
  const dest = resolve(webfontsDest, file);
  copyFileSync(src, dest);
  console.log(`copied ${src} -> ${dest}`);
}
