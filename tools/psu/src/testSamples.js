import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { liftPsu } from "./deobfuscator.js";

const root = path.resolve(process.cwd(), "samples");
const files = [];
const expectedFailures = new Map([
  ["4.5A/00a268c69f9c7d9f.lua", "unsupported handler/layout combination"],
  ["4.5A/043f045a8e0463ad.lua", "truncated source fixture"]
]);

function collect(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) collect(fullPath);
    if (entry.isFile() && entry.name.toLowerCase().endsWith(".lua")) files.push(fullPath);
  }
}

collect(root);
files.sort();

let passed = 0;
let failed = 0;
let expected = 0;

for (const file of files) {
  const relative = path.relative(root, file).replaceAll(path.sep, "/");
  try {
    const result = liftPsu(fs.readFileSync(file, "latin1"));
    if (expectedFailures.has(relative)) {
      failed += 1;
      console.error(`XPASS ${relative}`);
      continue;
    }
    passed += 1;
    console.log(`PASS ${relative} handlers=${result.stats.handlers}`);
  } catch (error) {
    if (expectedFailures.has(relative)) {
      expected += 1;
      console.log(`XFAIL ${relative} ${expectedFailures.get(relative)}`);
      continue;
    }
    failed += 1;
    console.error(`FAIL ${relative} ${error?.message || String(error)}`);
  }
}

console.log(`files=${files.length} passed=${passed} xfail=${expected} failed=${failed}`);
process.exitCode = failed === 0 ? 0 : 1;
