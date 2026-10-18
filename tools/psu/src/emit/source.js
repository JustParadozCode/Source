import { cleanDecompiledSource } from "./cleanup.js";

export function emitSource(source, options = {}) {
  if (options.raw) return `${source.trim()}\n`;
  return cleanDecompiledSource(source, options);
}
