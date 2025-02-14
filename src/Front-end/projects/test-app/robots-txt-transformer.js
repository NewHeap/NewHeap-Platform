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

let robotsTxtData = '';

const writeLine = (line) => {
  robotsTxtData += `${line}\n`;
};

if(environment === 'development') {
  writeLine('User-agent: *');
  writeLine('Disallow: /');
} else if(environment === 'staging') {
  writeLine('User-agent: *');
  writeLine('Disallow: /');
} else if(environment === 'production') {
  writeLine('User-agent: *');
}

fs.writeFileSync('./dist/test-app/browser/robots.txt', robotsTxtData,{ encoding: 'utf8', flag: 'w' });
console.log('robots.txt file has been updated.')
