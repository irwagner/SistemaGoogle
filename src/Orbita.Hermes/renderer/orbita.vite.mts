import { fileURLToPath } from 'node:url'
import { defineConfig, mergeConfig } from 'vite'

import hermesConfig from './vite.config.ts'

export default defineConfig((environment) => mergeConfig(hermesConfig(environment), {
  build: { outDir: 'dist-orbita', rollupOptions: { input: fileURLToPath(new URL('./orbita.html', import.meta.url)) } }
}))
