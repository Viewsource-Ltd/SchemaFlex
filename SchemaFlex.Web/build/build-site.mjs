// Builds the optimized static site that actually gets deployed to GitHub Pages:
// inlines site.css and the two JS files into the HTML (removing the round trips),
// minifies HTML/CSS/JS, and runs the favicon through SVGO. The source files stay
// separate on disk (css/site.css, js/*.js) because that's easier to edit; only the
// published output is merged. Run via `npm run build` from SchemaFlex.Web, or
// `node build/build-site.mjs <srcDir> <outDir>` from anywhere.
import { cp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import path from 'node:path';
import * as esbuild from 'esbuild';
import { minify as minifyHtml } from 'html-minifier-terser';
import { optimize as optimizeSvg } from 'svgo';

const [, , srcDirArg, outDirArg] = process.argv;
const srcDir = path.resolve(srcDirArg ?? '.');
const outDir = path.resolve(outDirArg ?? '_site');

// Files that exist only to build the site, or that are inlined and shouldn't be
// copied to the published output separately.
const EXCLUDED_TOP_LEVEL = new Set(['css', 'js', 'build', 'node_modules', 'package.json', 'package-lock.json']);
const HTML_FILES = ['index.html', 'privacy.html'];

async function minifyAsset(relativePath, loader) {
    const source = await readFile(path.join(srcDir, relativePath), 'utf8');
    const { code } = await esbuild.transform(source, { loader, minify: true });
    return code.trim();
}

async function main() {
    await rm(outDir, { recursive: true, force: true });
    await mkdir(outDir, { recursive: true });

    await cp(srcDir, outDir, {
        recursive: true,
        filter: (source) => {
            const rel = path.relative(srcDir, source);
            if (rel === '') return true;
            const topLevel = rel.split(path.sep)[0];
            return !EXCLUDED_TOP_LEVEL.has(topLevel);
        },
    });

    const css = await minifyAsset('css/site.css', 'css');
    const navJs = await minifyAsset('js/nav.js', 'js');
    const downloadJs = await minifyAsset('js/download.js', 'js');

    for (const file of HTML_FILES) {
        let html = await readFile(path.join(srcDir, file), 'utf8');

        html = html
            .replace('<link rel="stylesheet" href="/css/site.css" />', `<style>${css}</style>`)
            .replace('<script src="/js/nav.js" defer></script>', `<script>${navJs}</script>`)
            .replace('<script src="/js/download.js" defer></script>', `<script>${downloadJs}</script>`);

        html = await minifyHtml(html, {
            collapseWhitespace: true,
            removeComments: true,
            removeRedundantAttributes: true,
            removeScriptTypeAttributes: true,
            removeStyleLinkTypeAttributes: true,
            useShortDoctype: true,
            minifyCSS: true,
            minifyJS: true,
        });

        await writeFile(path.join(outDir, file), html);
    }

    const svg = await readFile(path.join(srcDir, 'favicon.svg'), 'utf8');
    const { data: optimizedSvg } = optimizeSvg(svg, { multipass: true });
    await writeFile(path.join(outDir, 'favicon.svg'), optimizedSvg);

    console.log(`Built optimized site: ${srcDir} -> ${outDir}`);
}

main().catch((err) => {
    console.error(err);
    process.exitCode = 1;
});
