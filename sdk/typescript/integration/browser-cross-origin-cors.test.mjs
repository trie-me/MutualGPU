import assert from "node:assert/strict";
import { existsSync } from "node:fs";
import test from "node:test";
import { chromium } from "playwright-core";

const apiBaseUrl = process.env.MUTUALGPU_API_URL ?? "https://mutualgpu.com";
const providerOrigin = process.env.MUTUALGPU_CORS_TEST_ORIGIN
  ?? "https://yosun-triposplat-webgpu-demo.static.hf.space";
const browserExecutable = process.env.MUTUALGPU_BROWSER_EXECUTABLE
  ?? "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";

test("credentialed browser fetch can read MutualGPU capabilities across origins", async () => {
  assert.ok(existsSync(browserExecutable),
    `Chromium was not found at ${browserExecutable}. Set MUTUALGPU_BROWSER_EXECUTABLE to a Chromium or Chrome executable.`);

  const browser = await chromium.launch({ executablePath: browserExecutable, headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();
  try {
    await page.goto(providerOrigin, { waitUntil: "domcontentloaded" });

    const result = await page.evaluate(async apiUrl => {
      const response = await fetch(new URL("/api/capabilities/", apiUrl), { credentials: "include" });
      return {
        status: response.status,
        contentType: response.headers.get("content-type"),
        body: await response.json()
      };
    }, apiBaseUrl);

    assert.equal(result.status, 200);
    assert.match(result.contentType ?? "", /^application\/json/i);
    assert.ok(Array.isArray(result.body));
  }
  finally {
    await context.close();
    await browser.close();
  }
}, { timeout: 30_000 });

test("credentialed browser fetch retains one requestor identity across origins", async () => {
  assert.ok(existsSync(browserExecutable),
    `Chromium was not found at ${browserExecutable}. Set MUTUALGPU_BROWSER_EXECUTABLE to a Chromium or Chrome executable.`);

  const browser = await chromium.launch({ executablePath: browserExecutable, headless: true });
  const context = await browser.newContext();
  const page = await context.newPage();
  try {
    await page.goto(providerOrigin, { waitUntil: "domcontentloaded" });

    const result = await page.evaluate(async apiUrl => {
      const request = path => fetch(new URL(path, apiUrl), { credentials: "include" });
      const bootstrap = await request("/");
      const first = await request("/api/tasks/");
      const second = await request("/api/tasks/");
      return {
        bootstrapStatus: bootstrap.status,
        first: { status: first.status, body: await first.text() },
        second: { status: second.status, body: await second.text() }
      };
    }, apiBaseUrl);

    assert.equal(result.bootstrapStatus, 200);
    assert.equal(result.first.status, 200, result.first.body);
    assert.equal(result.second.status, 200, result.second.body);
    assert.deepEqual(JSON.parse(result.first.body), JSON.parse(result.second.body));
  }
  finally {
    await context.close();
    await browser.close();
  }
}, { timeout: 30_000 });
