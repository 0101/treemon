import { defineConfig } from "vite";

export default defineConfig({
  root: "src/Client",
  build: {
    outDir: "../../dist",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/IWorktreeApi": "http://localhost:5000",
    },
  },
});
