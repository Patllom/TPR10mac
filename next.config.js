const configuredOrigin = process.env.TPR10_API_ORIGIN;
let apiOrigin;
if (configuredOrigin) {
  let url;
  try {
    url = new URL(configuredOrigin);
  } catch {
    throw new Error('TPR10_API_ORIGIN ต้องเป็น HTTP(S) origin ที่ถูกต้อง');
  }
  if (!['http:', 'https:'].includes(url.protocol) || url.pathname !== '/' ||
      url.search || url.hash || url.username || url.password) {
    throw new Error('TPR10_API_ORIGIN ต้องไม่มี path, query, fragment หรือ credentials');
  }
  apiOrigin = url.origin;
}
/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  async rewrites() {
    return apiOrigin
      ? [{ source: '/api/:path*', destination: apiOrigin + '/api/:path*' }]
      : [];
  },
};

module.exports = nextConfig;
