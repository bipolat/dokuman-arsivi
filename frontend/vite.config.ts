import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    // Dev sırasında API aynı origin'den geliyormuş gibi davranır: CORS derdi yok.
    proxy: {
      '/api': {
        target: 'http://localhost:5099',
        changeOrigin: true,
      },
    },
  },
  build: {
    // Build çıktısı doğrudan API'nin wwwroot'una gider: tek süreçte çalışabilsin.
    outDir: '../backend/DocArchive.Api/wwwroot',
    emptyOutDir: true,
  },
})
