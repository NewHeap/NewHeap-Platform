const apiTarget =
  process.env['services__sample-project-management-api__https__0'] ??
  'https://sample-project-management-api.dev.localhost:5281';

module.exports = [
  {
    // The API maps the assistant below /api/assistant itself, so this path is kept as is.
    context: ['/api/assistant'],
    target: apiTarget,
    secure: false,
    changeOrigin: true
  },
  {
    context: ['/api'],
    target: apiTarget,
    secure: false,
    changeOrigin: true,
    ws: true,
    pathRewrite: {
      '^/api': ''
    }
  }
];
