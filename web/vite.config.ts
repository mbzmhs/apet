import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// 构建产物 dist/ 由 apet-ui.csproj 拷进输出 wwwroot,随宿主一起分发;
// dev 模式代理 /api 到本地 Kestrel(端口与 WebServer 默认端口一致)。
export default defineConfig({
  plugins: [vue()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  server: {
    port: 5273,
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:17877',
        changeOrigin: false,
      },
    },
  },
})
