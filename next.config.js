/** @type {import('next').NextConfig} */
const nextConfig = {
  reactStrictMode: true,
  allowedDevOrigins: ['127.0.0.1'],
  outputFileTracingExcludes: {
    '/api/*': ['./.cache/**/*'],
    '/api/**/*': ['./.cache/**/*'],
  },
};

module.exports = nextConfig;
