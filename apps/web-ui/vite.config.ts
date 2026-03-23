import { defineConfig } from "vite";
import react from "@vitejs/plugin-react-swc";

// https://vitejs.dev/config/
export default defineConfig(() => {
  const port = Number(process.env.VITE_PORT ?? 4200) || 4200;
  return {
    define: {
      "process.platform": JSON.stringify(process.platform),
      "process.env": {},
    },
    plugins: [react()],
    base: "/",
    server: {
      port,
      host: "localhost",
    },
    preview: {
      port,
      host: "localhost",
    },
    build: {
      outDir: "dist",
    },
  };
});
