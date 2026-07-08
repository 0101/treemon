import { defineConfig } from "vite";
import { writeFileSync } from "fs";
import { resolve } from "path";

const vitePort = parseInt(process.env.VITE_PORT || "5173", 10);
const apiPort = parseInt(process.env.API_PORT || "5000", 10);
const canvasPort = parseInt(process.env.CANVAS_PORT || "5002", 10);

function versionJsonPlugin() {
  return {
    name: "version-json",
    writeBundle(options) {
      const outDir = options.dir || resolve("dist");
      const version = { buildTime: new Date().toISOString() };
      writeFileSync(resolve(outDir, "version.json"), JSON.stringify(version));
    },
  };
}

export default defineConfig({
  root: "src/Client",
  build: {
    outDir: "../../dist",
    emptyOutDir: true,
  },
  // The canvas-doc iframe origin baked into the client (read as CanvasPane.CanvasOrigin). Defaults to
  // the production 5002; the E2E test fixture sets CANVAS_PORT to its own free port so the client and
  // its test server agree without colliding with a running production Treemon.
  define: {
    __CANVAS_ORIGIN__: JSON.stringify(`http://127.0.0.1:${canvasPort}`),
  },
  plugins: [versionJsonPlugin()],
  server: {
    port: vitePort,
    proxy: {
      "/IWorktreeApi": `http://localhost:${apiPort}`,
    },
  },
});
