import { defineConfig } from "vite";

const vitePort = parseInt(process.env.VITE_PORT || "5173", 10);
const apiPort = parseInt(process.env.API_PORT || "5000", 10);

export default defineConfig({
  root: "src/Client",
  build: {
    outDir: "../../dist",
    emptyOutDir: true,
  },
  server: {
    port: vitePort,
    proxy: {
      "/IWorktreeApi": `http://localhost:${apiPort}`,
    },
  },
});
