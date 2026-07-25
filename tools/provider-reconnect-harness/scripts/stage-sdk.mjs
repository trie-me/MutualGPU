import { copyFile, mkdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const source = path.resolve(root, "../../artifacts/mutualgpu-browser-sdk-cancellation-preview-2026-07-25/mutualgpu-provider-sdk.js");
const destination = path.resolve(root, "public/sdk/mutualgpu-provider-sdk.js");

try {
  await stat(source);
} catch {
  try {
    await stat(destination);
    process.exit(0);
  } catch {
    throw new Error("The manual browser SDK preview is missing. Build or place it under artifacts/mutualgpu-browser-sdk-cancellation-preview-2026-07-25 before deploying this harness.");
  }
}

await mkdir(path.dirname(destination), { recursive: true });
await copyFile(source, destination);
