import { defineConfig } from "vite";
import { writeFileSync } from "fs";
import { resolve } from "path";

const vitePort = parseInt(process.env.VITE_PORT || "5173", 10);
const apiPort = parseInt(process.env.API_PORT || "5000", 10);

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
  plugins: [versionJsonPlugin()],
  server: {
    port: vitePort,
    proxy: {
      "/IWorktreeApi": `http://localhost:${apiPort}`,
    },
  },
});
