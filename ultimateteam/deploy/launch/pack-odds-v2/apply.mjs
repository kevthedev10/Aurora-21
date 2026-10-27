#!/usr/bin/env node
// Pack odds v2: stages values.json through the operator panel's content changeset, validates it, and publishes only
// with --publish. Run it on the operator's machine with the panel tunnel open:
//   ssh -i ~/.ssh/aurora21_csharp -N -L 18448:127.0.0.1:18448 root@<vps>
//   node apply.mjs              stage + validate, then discard (a dry run; the live catalogue is untouched)
//   node apply.mjs --keep       stage + validate and leave the changeset open to inspect or preview in the panel
//   node apply.mjs --publish    stage + validate + publish
// Deploy the image with the players-first rare allocation (MixedPackContents) first: these odds were modelled with it.
import fs from "node:fs";

const base = process.env.PANEL_API ?? "http://127.0.0.1:18448/panel/api";
const by = process.env.PANEL_BY ?? "pack-odds-v2";
const mode = process.argv.includes("--publish") ? "publish" : process.argv.includes("--keep") ? "keep" : "dry-run";
const values = JSON.parse(fs.readFileSync(new URL("./values.json", import.meta.url), "utf8"));

async function api(method, path, body) {
  const response = await fetch(base + path, {
    method, headers: body ? { "content-type": "application/json" } : {}, body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  const json = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(`${method} ${path} -> ${response.status}: ${text.slice(0, 400)}`);
  return json;
}

const open = await api("GET", "/content/changeset");
if ((open?.changeset?.changes?.length ?? 0) > 0)
  throw new Error("Another changeset is pending in the panel. Publish or discard it before applying pack odds v2.");

// Promo bucket weights: stage the whole definition with only the named buckets' weights changed.
for (const [code, weights] of Object.entries(values.promoWeights)) {
  const { definition } = await api("GET", `/promos/${code}`);
  for (const [bucket, weight] of Object.entries(weights)) {
    const target = definition.buckets.find(b => b.code === bucket);
    if (!target) throw new Error(`${code} has no bucket ${bucket}`);
    console.log(`${code}/${bucket}: ${target.draw_weight} -> ${weight}`);
    target.draw_weight = weight;
  }
  await api("PUT", "/content/changeset/changes", { area: "promo", key: { code }, operation: "update", after: definition, by });
}

// Rules rows (the players-only market switch).
for (const rule of values.rules ?? []) {
  console.log(`rules ${rule.file}/${rule.path.join(".")} -> ${JSON.stringify(rule.value)}`);
  await api("PUT", "/content/changeset/changes", { area: "rules", key: { file: rule.file, path: rule.path }, operation: "update",
    after: { value: rule.value }, by });
}

// Store packs: per-pack category chances, staged through the pack editor's own route (it merges them into the stored row).
const { revision } = await api("GET", "/packs/catalog");
for (const [packId, fields] of Object.entries(values.packs)) {
  console.log(`pack ${packId}: ${JSON.stringify(fields)}`);
  await api("PUT", `/packs/catalog/${packId}`, { revision, pack: fields });
}

const check = await api("POST", "/content/changeset/validate");
console.log(`validate: ok=${check.ok} changes=${check.changes} conflicts=${JSON.stringify(check.conflicts)} errors=${JSON.stringify(check.errors)}`);
if (!check.ok) { await api("POST", "/content/changeset/discard"); throw new Error("Validation failed; the changeset was discarded."); }

if (mode === "publish") {
  const result = await api("POST", "/content/changeset/publish", { note: "Pack odds v2 (2026-10-02): gold rare weights, TOTW and Icon chances", by });
  console.log(`published: revision ${result.content?.revision}`);
} else if (mode === "keep") {
  console.log("Left open: preview the pack odds in the panel (pending), then publish or discard there.");
} else {
  await api("POST", "/content/changeset/discard");
  console.log("Dry run: discarded. Re-run with --publish to apply.");
}
