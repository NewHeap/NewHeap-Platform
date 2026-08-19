const apiTarget =
  process.env['services__sample-project-management-api__https__0'] ??
  'https://sample-project-management-api.dev.localhost:5281';

module.exports = [
  {
    context: ['/api'],
    target: apiTarget,
    secure: false,
    changeOrigin: true,
    pathRewrite: {
      '^/api': ''
    }
  }
];
