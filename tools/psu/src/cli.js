#!/usr/bin/env node
import fs from "node:fs";
import path from "node:path";
import process from "node:process";
import { deobfuscateFile, liftFile } from "./deobfuscator.js";

function help() {
  console.log(`psu-deobf <input.lua> [options]

Options:
  -o, --output <file>      Write deobfuscated Lua to this path
      --bytecode <file>    Also write recovered Lua 5.1 bytecode
      --check              Decode and validate only; do not decompile
      --raw                Keep unluac variable names and raw formatting
      --unluac <file>      Path to unluac.jar
      --json               Print machine-readable stats
  -h, --help               Show this help

Examples:
  psu-deobf script.lua
  psu-deobf script.lua -o script.deobf.lua
  psu-deobf script.lua --check
  psu-deobf script.lua --bytecode script.luac`);
}

function parseArguments(argv) {
  const options = {
    input: null,
    output: null,
    bytecode: null,
    check: false,
    raw: false,
    unluac: null,
    json: false
  };

  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === "-h" || argument === "--help") {
      options.help = true;
    } else if (argument === "-o" || argument === "--output") {
      options.output = argv[++index] ?? null;
      if (!options.output) throw new Error(`${argument} requires a file path`);
    } else if (argument === "--bytecode") {
      options.bytecode = argv[++index] ?? null;
      if (!options.bytecode) throw new Error("--bytecode requires a file path");
    } else if (argument === "--unluac") {
      options.unluac = argv[++index] ?? null;
      if (!options.unluac) throw new Error("--unluac requires a file path");
    } else if (argument === "--check") {
      options.check = true;
    } else if (argument === "--raw") {
      options.raw = true;
    } else if (argument === "--json") {
      options.json = true;
    } else if (argument.startsWith("-")) {
      throw new Error(`Unknown option: ${argument}`);
    } else if (!options.input) {
      options.input = argument;
    } else {
      throw new Error(`Unexpected argument: ${argument}`);
    }
  }

  return options;
}

function defaultOutput(input) {
  const extension = path.extname(input);
  const base = path.basename(input, extension);
  return path.join(path.dirname(input), `${base}.deobf.lua`);
}

function printResult(result, options, outputPath) {
  const payload = {
    input: path.resolve(options.input),
    output: outputPath ? path.resolve(outputPath) : null,
    bytecode: options.bytecode ? path.resolve(options.bytecode) : null,
    ...result.stats
  };

  if (options.json) {
    console.log(JSON.stringify(payload, null, 2));
    return;
  }

  if (options.check) {
    console.log(`OK: ${options.input}`);
  } else {
    console.log(`Wrote ${outputPath}`);
  }
  if (options.bytecode) console.log(`Wrote ${options.bytecode}`);
  console.log(
    `handlers=${result.stats.handlers} layouts=${result.stats.layoutCandidates} unknown=${result.stats.unknownInstructions}`
  );
}

try {
  const options = parseArguments(process.argv.slice(2));
  if (options.help) {
    help();
    process.exit(0);
  }
  if (!options.input) {
    help();
    process.exit(2);
  }
  if (!fs.existsSync(options.input)) {
    throw new Error(`Input file not found: ${options.input}`);
  }

  const outputPath = options.check ? null : options.output ?? defaultOutput(options.input);
  const result = options.check
    ? liftFile(options.input)
    : deobfuscateFile(options.input, outputPath, options);

  if (options.bytecode) fs.writeFileSync(options.bytecode, result.bytecode);
  printResult(result, options, outputPath);
} catch (error) {
  console.error(error?.message || String(error));
  process.exit(1);
}
