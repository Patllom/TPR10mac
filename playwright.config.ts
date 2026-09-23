import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './tests/e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    browserName: 'firefox',
    baseURL: 'https://localhost:4443',
    ignoreHTTPSErrors: false,
    // ไม่บันทึก trace/video ซึ่งอาจมีรหัสผ่านหรือ recovery code
    trace: 'off',
    video: 'off',
    screenshot: 'off',
  },
});
