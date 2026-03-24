// Self-contained demo GIF recorder.
// Usage: node scripts/record-demo.mjs
//
// Starts the demo server + vite, waits for dashboard to load,
// captures screenshots at 15fps for one full 10s demo cycle,
// assembles into GIF via ffmpeg 2-pass palette, optimizes with gifsicle.
// Output: demo-screenshots.gif

import { chromium } from "playwright";
import { spawn, execSync } from "child_process";
import { existsSync, mkdirSync, rmSync, statSync, writeFileSync } from "fs";
import { resolve, dirname } from "path";
import { fileURLToPath } from "url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(__dirname, "..");
const tmpDir = resolve(ROOT, ".agents", "recording-tmp");

const SERVER_PORT = 5051;
const VITE_PORT = 5176;
const DEMO_URL = `http://localhost:${VITE_PORT}`;
const VIEWPORT = { width: 800, height: 1200 };
const LOOP_SECONDS = 10;
const FPS = 15;

const wingetLinks = resolve(process.env.LOCALAPPDATA, "Microsoft", "WinGet", "Links");
const PATH = `${wingetLinks};${process.env.PATH}`;
const env = { ...process.env, PATH, API_PORT: String(SERVER_PORT), VITE_PORT: String(VITE_PORT) };

function startProcess(label, command, args) {
  const proc = spawn(command, args, { cwd: ROOT, env, stdio: "pipe", shell: true });
  proc.stderr.on("data", (d) => {
    const msg = d.toString();
    if (!msg.includes("warn")) process.stderr.write(`[${label}] ${msg}`);
  });
  return proc;
}

async function waitForHttp(url, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try { await fetch(url); return; } catch {}
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`Timed out waiting for ${url}`);
}

function kill(proc) {
  if (!proc || proc.killed) return;
  try { execSync(`taskkill /PID ${proc.pid} /T /F`, { stdio: "ignore" }); }
  catch { proc.kill("SIGTERM"); }
}

// --- Main ---

if (existsSync(tmpDir)) rmSync(tmpDir, { recursive: true, force: true });
mkdirSync(tmpDir, { recursive: true });

const server = startProcess(
  "server", "dotnet",
  ["run", "--project", "src/Server", "--", "--demo", "--port", String(SERVER_PORT)]
);
const vite = startProcess("vite", "npx", ["vite", "--port", String(VITE_PORT)]);

const cleanup = () => { kill(server); kill(vite); };
process.on("SIGINT", cleanup);
process.on("SIGTERM", cleanup);
process.on("exit", cleanup);

try {
  console.log("Starting demo server and vite...");
  await Promise.all([
    waitForHttp(`http://localhost:${SERVER_PORT}/IWorktreeApi/GetWorktrees`),
    waitForHttp(DEMO_URL),
  ]);
  console.log("Both servers ready.");

  const browser = await chromium.launch({ headless: true });
  const page = await browser.newPage({ viewport: VIEWPORT });
  await page.goto(DEMO_URL, { waitUntil: "networkidle" });

  console.log("Waiting for dashboard to load...");
  await page.waitForFunction(
    () =>
      document.querySelectorAll(".wt-card.skeleton").length === 0 &&
      document.querySelectorAll(".wt-card:not(.skeleton)").length > 0,
    { timeout: 15000 }
  );
  await page.waitForTimeout(1500);
  console.log("Capturing screenshots...");

  const totalFrames = LOOP_SECONDS * FPS;
  const intervalMs = 1000 / FPS;
  for (let i = 0; i < totalFrames; i++) {
    const t0 = Date.now();
    const buf = await page.screenshot({ type: "png" });
    writeFileSync(resolve(tmpDir, `frame_${String(i).padStart(4, "0")}.png`), buf);
    const elapsed = Date.now() - t0;
    if (i < totalFrames - 1) {
      const wait = Math.max(0, intervalMs - elapsed);
      if (wait > 0) await page.waitForTimeout(wait);
    }
  }
  console.log(`Captured ${totalFrames} frames.`);
  await browser.close();

  // Assemble GIF via ffmpeg (2-pass palette for quality)
  const gifRaw = resolve(tmpDir, "raw.gif");
  const gifPath = resolve(ROOT, "demo-screenshots.gif");
  const palettePath = resolve(tmpDir, "palette.png");
  const input = resolve(tmpDir, "frame_%04d.png");
  const filters = `fps=${FPS},scale=${VIEWPORT.width}:-1:flags=lanczos`;

  console.log("Assembling GIF...");
  execSync(
    `ffmpeg -y -framerate ${FPS} -i "${input}" -vf "${filters},palettegen=stats_mode=diff" "${palettePath}"`,
    { stdio: "pipe", env }
  );
  execSync(
    `ffmpeg -y -framerate ${FPS} -i "${input}" -i "${palettePath}" -lavfi "${filters} [x]; [x][1:v] paletteuse=dither=bayer:bayer_scale=5:diff_mode=rectangle" "${gifRaw}"`,
    { stdio: "pipe", env }
  );

  const rawSize = statSync(gifRaw).size;

  console.log("Optimizing with gifsicle...");
  execSync(`npx gifsicle -O3 "${gifRaw}" -o "${gifPath}"`, { stdio: "pipe", cwd: ROOT });

  const finalSize = statSync(gifPath).size;
  const reduction = ((1 - finalSize / rawSize) * 100).toFixed(0);
  rmSync(tmpDir, { recursive: true, force: true });

  console.log(`\n✅ demo-screenshots.gif — ${(finalSize / 1024 / 1024).toFixed(1)} MB (${reduction}% optimized by gifsicle)`);
} finally {
  cleanup();
}
