import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import test from "node:test";
import { build } from "esbuild";
import { chromium } from "playwright-core";

const apiBaseUrl = required("MUTUALGPU_API_URL");
const providerKey = required("MUTUALGPU_PROVIDER_KEY");
const browserExecutable = process.env.MUTUALGPU_BROWSER_EXECUTABLE || "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

test("browser SDK enrolls then completes the WSS connection handshake in Chromium", async () => {
  assert.ok(existsSync(browserExecutable),
    `Chromium was not found at ${browserExecutable}. Set MUTUALGPU_BROWSER_EXECUTABLE to a Chromium or Chrome executable.`);

  const bundle = await buildBrowserSdkBundle();
  const browser = await chromium.launch({ executablePath: browserExecutable, headless: true });
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  const page = await context.newPage();
  try
  {
    // Load the API origin first. The SDK's fetch and WSS connection then execute
    // in the same browser origin as a real hosted provider page, rather than in Node.
    await page.goto(apiBaseUrl, { waitUntil: "domcontentloaded" });
    await page.addScriptTag({ content: bundle });

    await page.evaluate(async ({ apiUrl, key, definition }) => {
      const { BrowserWebSocketTransport, ProviderClient } = globalThis.MutualGpuBrowserSdk;
      const provider = new ProviderClient(new BrowserWebSocketTransport(apiUrl, key));
      await provider.enroll(definition);
      await provider.connect(async () => {});
      provider.close();
    }, { apiUrl: apiBaseUrl, key: providerKey, definition: canonicalDefinition() });
  }
  finally
  {
    await context.close();
    await browser.close();
  }
}, { timeout: 30_000 });

async function buildBrowserSdkBundle()
{
  const result = await build({
    bundle: true,
    format: "iife",
    globalName: "MutualGpuBrowserSdk",
    platform: "browser",
    write: false,
    stdin: {
      resolveDir: new URL(".", import.meta.url).pathname,
      sourcefile: "browser-sdk-entry.mjs",
      contents: [
        'export { ProviderClient } from "@mutualgpu/provider-core";',
        'export { BrowserWebSocketTransport } from "@mutualgpu/provider-web";'
      ].join("\n")
    }
  });
  return result.outputFiles[0].text;
}

function canonicalDefinition()
{
  return {
    machine: { tier: "Large", specifications: { computeTier: "Large", memoryGiB: 32 } },
    capabilities: [{
      name: "tripo-splat",
      inputs: [
        { key: "image_url", type: "Image", required: true, label: "Image", description: "Input image to convert into a 3D Gaussian splat.", contentTypes: ["image/png", "image/jpeg", "image/webp"] },
        { key: "num_gaussians", type: "Integer", required: false, label: "Number of Gaussians", description: "Target Gaussian count; the provider rounds it to a multiple of 32.", default: "262144" },
        { key: "num_inference_steps", type: "Integer", required: false, label: "Inference steps", description: "Flow-matching sampler steps. More steps improve fidelity with roughly linear runtime cost.", default: "20" },
        { key: "guidance_scale", type: "Number", required: false, label: "Guidance scale", description: "Classifier-free guidance strength; values at or below 1 disable guidance.", default: "3" },
        { key: "output_format", type: "String", required: false, label: "Output format", description: "Generated Gaussian-splat file format.", default: "ply", allowedValues: ["ply", "splat"] },
        { key: "seed", type: "Integer", required: false, label: "Seed", description: "Optional random seed for reproducible output. Leave empty for a random seed." },
        { key: "enable_safety_checker", type: "Boolean", required: false, label: "Enable safety checker", description: "The browser provider does not bundle a qualified safety checker; submit false.", default: "false" }
      ],
      output: { hasMetadata: true },
      description: "TripoSplat-style image-to-splat form exercised by the local exchange demo."
    }]
  };
}

function required(name)
{
  const value = process.env[name];
  if (!value) throw new Error(`${name} is required for the browser integration test.`);
  return value;
}
