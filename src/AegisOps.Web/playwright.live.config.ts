import { defineConfig } from "@playwright/test";

// No @types/node in this package: read the environment through a typed view of globalThis.process.
const env = (globalThis as { process?: { env: Record<string, string | undefined> } }).process?.env ?? {};

// Live checks against a running Compose stack (no mocks, no dev server). Never part of `npm run test:e2e` or CI:
// they need real demo accounts and create (then cancel) a deployment. See e2e-live/*.live.spec.ts.
export default defineConfig({
  testDir: "./e2e-live",
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: "list",
  use: {
    baseURL: env.AEGISOPS_LIVE_URL ?? "http://localhost:3000",
    browserName: "chromium",
    headless: env.AEGISOPS_LIVE_HEADED !== "1",
    serviceWorkers: "block",
    // No traces: they would store the sign-in request bodies, i.e. the demo passwords.
    trace: "off",
    // Specs create their own contexts and set recordVideo from AEGISOPS_LIVE_VIDEO themselves.
  },
});
