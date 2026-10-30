'use strict';
const crypto = require('node:crypto');
const SCHEMA_VERSION = 1;
const LOCATION_FIELDS = ['loc', 'house', 'store', 'ship', 'settlement', 'city'];
const MISSING = new Set(['', 'varies', 'unknown', 'n/a', 'none', '?', 'unspecified']);
function rawOrNull(value) {
  if (value === null || value === undefined) return null;
  const trimmed = String(value).trim();
  return trimmed === '' ? null : trimmed;
}
function plain(value) {
  const raw = rawOrNull(value);
  if (!raw || MISSING.has(raw.toLowerCase())) return null;
  const link = /^\[\[(?:(?:Online|ON):)?([^|\]#]+)(?:\|([^\]]+))?\]\]$/i.exec(raw);
  if (link) return rawOrNull(link[2] ?? link[1]);
  if (/[{}\[\]<>]/.test(raw)) return null;
  return raw;
}
function chooseConsensus(variants, field, convert = plain) {
  const values = [...new Set(variants.map(v => convert(v.fields?.[field])).filter(v => v !== null))];
  return { value: values.length === 1 ? values[0] : null, conflict: values.length > 1 ? values : null };
}
function nullableInteger(value) {
  const text = rawOrNull(value);
  if(!text || !/^(?:0|[1-9][0-9]*|[1-9][0-9]{0,2}(?:,[0-9]{3})+)$/.test(text))return null;
  const parsed=Number(text.replaceAll(',',''));
  return Number.isSafeInteger(parsed)?parsed:null;
}
function factionName(raw) {
  if (!raw) return null;
  const match = /^\{\{Faction\|([^|}]+)(?:\|[^}]*)?\}\}/i.exec(raw);
  return match ? rawOrNull(match[1]) : plain(raw);
}
function resolvePage(rawName, pages, prefix = 'Online:') {
  const name = rawOrNull(rawName);
  if (!name) return null;
  const title = /^(Online|ON|Lore):/i.test(name) ? name.replace(/^ON:/i, 'Online:') : prefix + name;
  return pages.get(title.toLowerCase()) ?? null;
}
function urlFor(title) {
  return 'https://en.uesp.net/wiki/' + title.replaceAll(' ', '_').split('/').map(x => encodeURIComponent(x).replace('%3A', ':')).join('/');
}
function extractCandidates(raw) {
  if (!raw) return [];
  const matches = [...raw.matchAll(/\[\[(?:(?:Online|ON):)([^|\]#]+)(?:#[^|\]]+)?(?:\|[^\]]*)?\]\]/gi)];
  if (matches.length) return [...new Set(matches.map(m => m[1].trim()))];
  const single = plain(raw);
  return single ? [single] : [];
}
function locationsFrom(variants, pages) {
  const candidates = [];
  const seen = new Set();
  for (const variant of variants) {
    for (const field of LOCATION_FIELDS) {
      const raw = rawOrNull(variant.fields?.[field]);
      for (const name of extractCandidates(raw)) {
        const resolved = resolvePage(name, pages);
        const entry = { name, placeId: resolved?.pageId ?? null, pageTitle: resolved?.title ?? null,
          relation: field, summaryNumber: variant.number, raw, resolution: resolved ? 'exact_title' : 'unresolved' };
        const key = [entry.summaryNumber, field, entry.pageTitle ?? name].join('|').toLowerCase();
        if (!seen.has(key)) { candidates.push(entry); seen.add(key); }
      }
    }
  }
  let primary = null;
  let ambiguous = false;
  for (const field of LOCATION_FIELDS) {
    const entries = candidates.filter(c => c.relation === field);
    if (!entries.length) continue;
    const unique = new Map(entries.map(c => [c.pageTitle??('unresolved:'+c.name.toLowerCase()),c]));
    // One resolved place plus an unresolved alternative is NOT unambiguous.
    if(unique.size === 1 && entries.every(c=>c.placeId!==null))
      primary = [...unique.values()][0];
    else ambiguous = true;
    break;
  }
  return { primary, candidates, ambiguous };
}
function hashId(id) {
  return crypto.createHash('sha256').update(id, 'utf8').digest('hex');
}
module.exports = { SCHEMA_VERSION, LOCATION_FIELDS, rawOrNull, plain, chooseConsensus,
  nullableInteger, factionName, resolvePage, urlFor, extractCandidates, locationsFrom, hashId };
