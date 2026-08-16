import { defineConfig } from "tsup";

export default defineConfig({
  entry: ["src/index.ts"],
  format: ["esm"],
  dts: true,
  sourcemap: true,
  clean: true,
  // React is a peer dep — the host app supplies it, this kit never bundles it.
  external: ["react", "react-dom"],
  // The self-hosted IBM Plex Sans woff2 is referenced from tokens.css via
  // url(); dataurl inlines it straight into the emitted CSS so the built
  // package is one self-contained stylesheet with no second file whose path
  // could go stale wherever this kit ends up bundled.
  loader: {
    ".woff2": "dataurl",
  },
});
