fs = require('fs');

const startArguments = process.argv.slice(2);
let environment = '';

for(const startArgument of startArguments) {
  if(startArgument.includes('environment=')) {
    environment = startArgument.replace('environment=', '').trim().toLowerCase();
  }
}

if(environment !== 'development' && environment !== 'staging' && environment !== 'production') {
  console.error('Invalid environment. Please provide a valid environment.');
  process.exit();
}

const PLACEHOLDER_HEADER_CSP = '{{{{{Content-Security-Policy}}}}}';
let webConfigData = fs.readFileSync('projects/test-app/src/web.config', 'utf8');

const setCspHeader = () => {
  const cspHeaderData = {
    defaultSrc: [
      "'self'"
    ],
    imageSrc: [
      "'self'",
      "data:",
    ],
    styleSrc: [
      "'self'",
      "'unsafe-inline'",
    ],
    scriptSrc: [
      "'self'",
      "'nonce-{{{NH_CSP_NONCE_PLACEHOLDER}}}'",
      'https://sentry.io',
      'https://*.sentry.io',
    ],
    fontSrc: [
      "'self'"
    ],
    connectSrc: [
      "'self'",
      "ws:",
      'https://sentry.io',
      'https://*.sentry.io'
    ],
    frameSrc: [
      "'self'",
      "https://www.youtube.com/"
    ]
  };

  if(environment === 'development') {
    cspHeaderData.connectSrc.push('localhost:4200');
  } else if(environment === 'staging') {
    cspHeaderData.connectSrc.push('https://todo');
  } else if(environment === 'production') {
    cspHeaderData.connectSrc.push('https://todo');
  }

  const cspHeaderValues = [];
  cspHeaderValues.push(`default-src ${cspHeaderData.defaultSrc.join(' ').trim()}`);
  cspHeaderValues.push(`style-src ${cspHeaderData.styleSrc.join(' ').trim()}`);
  cspHeaderValues.push(`script-src ${cspHeaderData.scriptSrc.join(' ').trim()}`);
  cspHeaderValues.push(`font-src ${cspHeaderData.fontSrc.join(' ').trim()}`);
  cspHeaderValues.push(`connect-src ${cspHeaderData.connectSrc.join(' ').trim()}`);
  cspHeaderValues.push(`img-src ${cspHeaderData.imageSrc.join(' ').trim()}`);
  cspHeaderValues.push(`frame-src ${cspHeaderData.frameSrc.join(' ').trim()}`);

  const cspHeaderValue = cspHeaderValues.join('; ').trim();
  webConfigData = webConfigData.replaceAll(PLACEHOLDER_HEADER_CSP, cspHeaderValue);
};

setCspHeader();

fs.writeFileSync('./dist/test-app/browser/web.config', webConfigData,{ encoding: 'utf8', flag: 'w' });
console.log('Web.config file has been updated.')
