import { build } from "esbuild";
import { fileURLToPath } from "node:url";
import path from "node:path";

const sdkRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const output = path.resolve(sdkRoot, "../../src/MutualGPU.Api/wwwroot/js/mutualgpu-provider-sdk.js");

await build({
  bundle: true,
  entryPoints: [path.resolve(sdkRoot, "scripts/browser-bundle-entry.mjs")],
  format: "esm",
  platform: "browser",
  outfile: output,
  sourcemap: false,
});
