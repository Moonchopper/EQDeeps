import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// Build output goes straight into the backend's wwwroot so `dotnet run` serves
// the SPA; in dev, Vite proxies API + hub traffic to the local backend.
export default defineConfig({
  plugins: [react()],
  build: {
    outDir: "../src/EQDeeps.Server/wwwroot",
    emptyOutDir: true,
  },
  server: {
    proxy: {
      "/api": "http://127.0.0.1:5487",
      "/hubs": {
        target: "http://127.0.0.1:5487",
        ws: true,
      },
    },
  },
});
