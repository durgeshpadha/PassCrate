import { defineConfig } from 'vite';
import tailwindcss from '@tailwindcss/vite';
export default defineConfig({
  plugins: [tailwindcss()],
  build: {
    outDir: '../wwwroot/dist',
    emptyOutDir: true,
    rollupOptions: {
      input: 'src/main.jsx',
      output: { entryFileNames: 'site.js', assetFileNames: 'site.[ext]' },
    },
  },
});
