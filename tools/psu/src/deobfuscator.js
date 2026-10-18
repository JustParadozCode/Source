import fs from "node:fs";
import { analyzeWrapper } from "./core/wrapperAnalyzer.js";
import { decodePayload } from "./core/payload.js";
import { decodeChunkPayload } from "./core/chunkDecoder.js";
import {
  classifyHandlers,
  expandSuperinstructions,
  scoreChunkSemantics
} from "./core/handlerClassifier.js";
import { lowerToLua51 } from "./ir/lua51Lowerer.js";
import { writeLua51Chunk } from "./bytecode/lua51Writer.js";
import { decompileLua51 } from "./decompile/unluac.js";
import { emitSource } from "./emit/source.js";

function countUnknownInstructions(chunk) {
  let count = chunk.instructions.filter(
    (instruction) => instruction.kind === "unknown"
  ).length;
  for (const proto of chunk.protos) count += countUnknownInstructions(proto);
  return count;
}

function validateInput(source) {
  if (!source.includes("PSU|")) {
    throw new Error("Input does not contain a PSU payload");
  }
}

export function liftPsu(source) {
  validateInput(source);

  const analysis = classifyHandlers(analyzeWrapper(source));
  const decoded = decodeChunkPayload(
    decodePayload(source),
    analysis.tags,
    (chunk) => scoreChunkSemantics(chunk, analysis)
  );
  const expanded = expandSuperinstructions(decoded.chunk, analysis);
  const unknownInstructions = countUnknownInstructions(expanded);

  if (unknownInstructions !== 0) {
    throw new Error(
      `PSU devirtualization left ${unknownInstructions} unsupported instruction${unknownInstructions === 1 ? "" : "s"}`
    );
  }

  const bytecode = writeLua51Chunk(lowerToLua51(expanded));

  return {
    bytecode,
    stats: {
      unknownInstructions,
      initialKey: decoded.initialKey,
      chunkOrder: decoded.chunkOrder,
      instructionOrder: decoded.instructionOrder,
      refinedInstructionOrders: decoded.refinedInstructionOrders,
      layoutCandidates: decoded.candidateCount,
      handlers: analysis.handlers.size,
      fields: analysis.fields
    }
  };
}

export function deobfuscatePsu(source, options = {}) {
  const lifted = liftPsu(source);
  const decompiled = decompileLua51(lifted.bytecode, {
    jarPath: options.unluac,
    javaPath: options.java,
    timeout: options.timeout
  });
  const code = emitSource(decompiled, {
    raw: options.raw,
    renameVariables: options.renameVariables !== false,
    foldChains: options.foldChains !== false
  });

  return {
    ...lifted,
    code
  };
}

export function liftFile(inputPath) {
  return liftPsu(fs.readFileSync(inputPath, "latin1"));
}

export function deobfuscateFile(inputPath, outputPath, options = {}) {
  const result = deobfuscatePsu(fs.readFileSync(inputPath, "latin1"), options);
  fs.writeFileSync(outputPath, result.code, "utf8");
  return result;
}
