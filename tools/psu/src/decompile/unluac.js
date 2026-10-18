import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const moduleDirectory = path.dirname(fileURLToPath(import.meta.url));

function findJar(explicitPath) {
  const candidates = [
    explicitPath,
    process.env.UNLUAC_JAR,
    path.resolve(moduleDirectory, "../../tools/unluac.jar")
  ].filter(Boolean);

  const found = candidates.find((candidate) => fs.existsSync(candidate));
  if (found) return found;

  throw new Error(
    "unluac.jar was not found. Put it at tools/unluac.jar or set UNLUAC_JAR."
  );
}

export function decompileLua51(bytecode, options = {}) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), "psu-deobf-"));
  const input = path.join(directory, "payload.luac");

  try {
    fs.writeFileSync(input, bytecode);
    const result = spawnSync(
      options.javaPath || process.env.JAVA_EXE || "java",
      ["-jar", findJar(options.jarPath), "--nodebug", input],
      {
        encoding: "utf8",
        windowsHide: true,
        timeout: options.timeout ?? 120_000,
        maxBuffer: 64 * 1024 * 1024
      }
    );

    if (result.error) throw result.error;
    if (result.status !== 0 || !result.stdout?.trim()) {
      throw new Error(result.stderr?.trim() || "Lua 5.1 bytecode decompilation failed");
    }
    return result.stdout.trim();
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
}
