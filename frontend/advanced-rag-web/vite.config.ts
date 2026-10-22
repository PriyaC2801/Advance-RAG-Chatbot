import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5190,
    strictPort: true,
    proxy: { '/api': { target: 'http://localhost:5090', changeOrigin: true } },
  },
});

