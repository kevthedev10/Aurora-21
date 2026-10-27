#!/usr/bin/env node
// Aurora21 UltimateTeam — wire writer generator.
//
// Reads the embedded response templates (resolved relative to this file, never the shell cwd)
// and emits C# that writes each template's shape straight to a Utf8JsonWriter, skipping the
// JsonObject tree that JsonTemplate has to build today.
//
// The templates stay the source of truth for every client-visible shape; the C# is a derived
// build artifact. Node built-ins only. No timestamps, no randomness: running twice must produce
// byte-identical output, so a template edited without regenerating fails the manifest check.
//
// Literal classification mirrors JsonMaterializer exactly (a number with no '.' and no exponent
// that fits in Int64 is a long, anything else is a double) and encoding mirrors FutJson's
// UnsafeRelaxedJsonEscaping. Any drift there shows up as a byte difference in the parity test.

import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { fileURLToPath } from "node:url";

const TOOL_DIR = path.dirname(fileURLToPath(import.meta.url));
const CORE_DIR = path.resolve(TOOL_DIR, "..", "..", "src", "Aurora21.UltimateTeam.Core");
const TEMPLATE_DIR = path.join(CORE_DIR, "Resources", "Templates");
const OUTPUT_FILE = path.join(CORE_DIR, "Helpers", "Json", "WireWriters.g.cs");
const MANIFEST_FILE = path.join(TOOL_DIR, "wire-writers-manifest.json");
const PLACEHOLDER = /^\{\{([A-Za-z][A-Za-z0-9_]*)\}\}$/;

function fail(message) {
  console.error("generate-wire-writers: FATAL: " + message);
  process.exit(1);
}

const sha256 = (buffer) => crypto.createHash("sha256").update(buffer).digest("hex");

// ---------------------------------------------------------------------------
// JSON reader that keeps what JSON.parse discards: the raw text of every number
// (so 1.0 stays a double) and the written order of every key (JavaScript objects
// reorder integer-like keys, and key order is part of the wire contract).
// ---------------------------------------------------------------------------
function parseJson(text, file) {
  let at = 0;
  const error = (message) => fail(`${file}: ${message} at offset ${at}`);
  const ws = () => { while (at < text.length && " \t\r\n".includes(text[at])) at++; };

  function value() {
    ws();
    const ch = text[at];
    if (ch === "{") return object();
    if (ch === "[") return array();
    if (ch === '"') return { kind: "string", value: string() };
    if (ch === "t") { expect("true"); return { kind: "bool", value: true }; }
    if (ch === "f") { expect("false"); return { kind: "bool", value: false }; }
    if (ch === "n") { expect("null"); return { kind: "null" }; }
    return number();
  }
  function expect(word) {
    if (text.slice(at, at + word.length) !== word) error(`expected ${word}`);
    at += word.length;
  }
  function object() {
    at++; // {
    const entries = [];
    ws();
    if (text[at] === "}") { at++; return { kind: "object", entries }; }
    for (;;) {
      ws();
      if (text[at] !== '"') error("expected a key");
      const key = string();
      ws();
      if (text[at] !== ":") error("expected ':'");
      at++;
      entries.push([key, value()]);
      ws();
      if (text[at] === ",") { at++; continue; }
      if (text[at] === "}") { at++; return { kind: "object", entries }; }
      error("expected ',' or '}'");
    }
  }
  function array() {
    at++; // [
    const items = [];
    ws();
    if (text[at] === "]") { at++; return { kind: "array", items }; }
    for (;;) {
      items.push(value());
      ws();
      if (text[at] === ",") { at++; continue; }
      if (text[at] === "]") { at++; return { kind: "array", items }; }
      error("expected ',' or ']'");
    }
  }
  function string() {
    at++; // opening quote
    let out = "";
    for (;;) {
      const ch = text[at++];
      if (ch === undefined) error("unterminated string");
      if (ch === '"') return out;
      if (ch !== "\\") { out += ch; continue; }
      const escape = text[at++];
      if (escape === "u") { out += String.fromCharCode(parseInt(text.slice(at, at + 4), 16)); at += 4; continue; }
      const simple = { '"': '"', "\\": "\\", "/": "/", b: "\b", f: "\f", n: "\n", r: "\r", t: "\t" }[escape];
      if (simple === undefined) error("bad escape");
      out += simple;
    }
  }
  function number() {
    const start = at;
    if (text[at] === "-") at++;
    while (at < text.length && "0123456789.eE+-".includes(text[at])) at++;
    const raw = text.slice(start, at);
    if (!/^-?\d+(\.\d+)?([eE][-+]?\d+)?$/.test(raw)) error(`bad number '${raw}'`);
    return { kind: "number", raw };
  }

  const root = value();
  ws();
  if (at !== text.length) error("trailing content");
  return root;
}

// JsonMaterializer: no '.' and no exponent and it fits Int64 -> long, otherwise double.
function isLongLiteral(raw) {
  if (raw.includes(".") || raw.includes("e") || raw.includes("E")) return false;
  try { return BigInt(raw) >= -(2n ** 63n) && BigInt(raw) < 2n ** 63n; } catch { return false; }
}

// ---------------------------------------------------------------------------
// Emit.
// ---------------------------------------------------------------------------
const csharpString = (value) => '"' + [...value].map((ch) => {
  if (ch === '"') return '\\"';
  if (ch === "\\") return "\\\\";
  const code = ch.codePointAt(0);
  if (code < 0x20 || code > 0x7e) return "\\u" + code.toString(16).padStart(4, "0");
  return ch;
}).join("") + '"';

const names = new Map();  // encoded property name / literal string -> field
const doubles = new Map(); // raw double text -> field
function nameField(text) {
  if (!names.has(text)) names.set(text, "T" + names.size);
  return names.get(text);
}
function doubleField(raw) {
  if (!doubles.has(raw)) doubles.set(raw, "D" + doubles.size);
  return doubles.get(raw);
}

function methodName(template) {
  const parts = template.split(/[\/\-_.]/).filter(Boolean);
  return "Write" + parts.map((p) => p[0].toUpperCase() + p.slice(1)).join("");
}

const placeholdersOf = new Map();

// `property` is the field holding this value's key, or null when writing an array element.
function emit(node, property, indent, lines, seen) {
  const pad = "    ".repeat(indent);
  const key = property === null ? "" : property + ", ";
  const valueSuffix = property === null ? "Value" : "";
  switch (node.kind) {
    case "object": {
      lines.push(`${pad}w.WriteStartObject(${property === null ? "" : property});`);
      for (const [entryKey, entryValue] of node.entries) emit(entryValue, nameField(entryKey), indent, lines, seen);
      lines.push(`${pad}w.WriteEndObject();`);
      return;
    }
    case "array": {
      lines.push(`${pad}w.WriteStartArray(${property === null ? "" : property});`);
      for (const item of node.items) emit(item, null, indent, lines, seen);
      lines.push(`${pad}w.WriteEndArray();`);
      return;
    }
    case "string": {
      const match = PLACEHOLDER.exec(node.value);
      if (match) {
        seen.add(match[1]);
        if (property !== null) lines.push(`${pad}w.WritePropertyName(${property});`);
        // Through TemplateArgs, not FutJson.WriteValue(w, a[name]): the argument may be a deferred
        // writer with no node form, and TemplateArgs.WriteTo writes either kind the same way.
        lines.push(`${pad}a.WriteTo(w, ${csharpString(match[1])});`);
        return;
      }
      lines.push(`${pad}w.WriteString${valueSuffix}(${key}${nameField(node.value)});`);
      return;
    }
    case "number": {
      if (isLongLiteral(node.raw)) {
        lines.push(`${pad}w.WriteNumber${valueSuffix}(${key}${node.raw}L);`);
      } else {
        // FutJson writes non-integers through FormatDouble, so reuse it rather than
        // reimplementing its shortest-round-trip rules here.
        if (property !== null) lines.push(`${pad}w.WritePropertyName(${property});`);
        lines.push(`${pad}w.WriteRawValue(${doubleField(node.raw)}, true);`);
      }
      return;
    }
    case "bool":
      lines.push(`${pad}w.WriteBoolean${valueSuffix}(${key}${node.value});`);
      return;
    case "null":
      lines.push(property === null ? `${pad}w.WriteNullValue();` : `${pad}w.WriteNull(${property});`);
      return;
    default:
      fail("unknown node kind " + node.kind);
  }
}

// ---------------------------------------------------------------------------
// Walk the templates.
// ---------------------------------------------------------------------------
function templateFiles(dir, prefix = "") {
  const out = [];
  for (const entry of fs.readdirSync(dir, { withFileTypes: true }).sort((a, b) => (a.name < b.name ? -1 : 1))) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) out.push(...templateFiles(full, prefix + entry.name + "/"));
    else if (entry.name.endsWith(".json")) out.push({ name: prefix + entry.name.slice(0, -5), file: full });
  }
  return out;
}

const templates = templateFiles(TEMPLATE_DIR);
if (templates.length === 0) fail("no templates found under " + TEMPLATE_DIR);

const bodies = [];
const dispatch = [];
const inputs = [];
const usedMethods = new Set();

for (const { name, file } of templates) {
  const bytes = fs.readFileSync(file);
  // The loader normalises line endings before parsing, so hash the same text it parses.
  const text = bytes.toString("utf8").replace(/\r\n/g, "\n");
  const root = parseJson(text, name);
  const method = methodName(name);
  if (usedMethods.has(method)) fail(`two templates map to ${method}; rename one of them`);
  usedMethods.add(method);

  const seen = new Set();
  const lines = [];
  emit(root, null, 2, lines, seen);
  placeholdersOf.set(name, [...seen].sort());

  bodies.push(
    `    /// <summary>Writes <c>Resources/Templates/${name}.json</c>.</summary>\n` +
    `    public static void ${method}(Utf8JsonWriter w, TemplateArgs a)\n` +
    `    {\n${lines.join("\n")}\n    }`
  );
  dispatch.push(`            case ${csharpString(name)}: ${method}(writer, args); return true;`);
  inputs.push({ template: name, sha256: sha256(Buffer.from(text, "utf8")), placeholders: placeholdersOf.get(name) });
}

const nameFields = [...names.entries()].map(([text, field]) =>
  `    private static readonly JsonEncodedText ${field} = JsonEncodedText.Encode(${csharpString(text)}, Encoder);`);
const doubleFields = [...doubles.entries()].map(([raw, field]) =>
  `    private static readonly string ${field} = FutJson.FormatDouble(${raw});`);

const source = `// <auto-generated/>
// Generated by tools/wire-writers/generate.mjs from Resources/Templates. Do not edit by hand:
// change the template and regenerate, or the manifest check fails.
//
// Each method writes one template's shape straight to the writer. Literal text and property names
// are pre-encoded once; only placeholder values are written from the caller's arguments.
#nullable enable
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Aurora21.UltimateTeam.Core.Helpers.Json;

/// <summary>
/// Direct-to-writer forms of the embedded response templates, emitting the same bytes as
/// <see cref="JsonTemplate"/> rendered through <see cref="FutJson"/> without building a node tree.
/// </summary>
public static class WireWriters
{
    private static readonly JavaScriptEncoder Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

${nameFields.join("\n")}

${doubleFields.join("\n")}

    /// <summary>Every template this class can write, in generated order.</summary>
    public static readonly string[] Templates =
    {
${templates.map((t) => `        ${csharpString(t.name)},`).join("\n")}
    };

    /// <summary>Writes the named template, or returns false when it has no generated writer.</summary>
    public static bool TryWrite(string template, Utf8JsonWriter writer, TemplateArgs args)
    {
        switch (template)
        {
${dispatch.join("\n")}
            default: return false;
        }
    }

${bodies.join("\n\n")}
}
`;

fs.mkdirSync(path.dirname(OUTPUT_FILE), { recursive: true });
fs.writeFileSync(OUTPUT_FILE, source, "utf8");

const manifest = {
  formatVersion: 1,
  generator: "tools/wire-writers/generate.mjs",
  sourceDirectory: "src/Aurora21.UltimateTeam.Core/Resources/Templates",
  output: {
    path: "src/Aurora21.UltimateTeam.Core/Helpers/Json/WireWriters.g.cs",
    sha256: sha256(Buffer.from(source, "utf8")),
    bytes: Buffer.byteLength(source, "utf8"),
  },
  templates: inputs,
};
fs.writeFileSync(MANIFEST_FILE, JSON.stringify(manifest, null, 2) + "\n", "utf8");

console.log(`wire writers: ${templates.length} templates, ${names.size} encoded literals, ` +
  `${doubles.size} doubles, ${Buffer.byteLength(source, "utf8")} bytes`);
