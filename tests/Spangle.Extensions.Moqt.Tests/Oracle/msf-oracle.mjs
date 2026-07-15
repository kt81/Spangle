// Parses a catalog with moq-playa's MSF implementation and reports what it made of it.
//
// This is an oracle, not a test helper: the point is that none of this code is ours. moq-playa's
// @moqt/msf is an independent implementation of draft-ietf-moq-msf-00, written from the draft by
// someone else, and it is the parser a browser player actually runs. A catalog it accepts is a
// catalog a player can act on; one it rejects is a broadcast nobody can watch, however well-formed
// the JSON looks to us. A relay never reads the catalog, so this is the only implementation besides
// our own that ever sees the document.
//
//   node msf-oracle.mjs <playa-repo-dir> <catalog.json> [catalog-namespace]
//
// Prints the parsed catalog as JSON on success; prints the parser's own complaint and exits 1 on
// rejection. Requires the package to have been built: pnpm --filter @moqt/msf build

import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import { join } from 'node:path';

const [playaDir, catalogPath, catalogNamespace] = process.argv.slice(2);
if (!playaDir || !catalogPath) {
    console.error('usage: node msf-oracle.mjs <playa-repo-dir> <catalog.json> [catalog-namespace]');
    process.exit(2);
}

const msfEntry = pathToFileURL(join(playaDir, 'packages', 'msf', 'dist', 'index.js')).href;
const { parseMsfCatalog, CATALOG_TRACK_NAME, MSF_VERSION } = await import(msfEntry);

const json = readFileSync(catalogPath, 'utf8');
try {
    const catalog = parseMsfCatalog(json, catalogNamespace);
    console.log(JSON.stringify({
        ok: true,
        // Echoed back so the caller can check its own constants against theirs rather than trusting
        // that both sides read the same draft.
        catalogTrackName: CATALOG_TRACK_NAME,
        msfVersion: MSF_VERSION,
        catalog,
    }));
} catch (error) {
    console.log(JSON.stringify({ ok: false, error: String(error && error.message ? error.message : error) }));
    process.exit(1);
}
