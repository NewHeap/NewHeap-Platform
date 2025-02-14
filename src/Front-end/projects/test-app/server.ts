import {
  AngularNodeAppEngine,
  createNodeRequestHandler,
  isMainModule,
  writeResponseToNodeResponse
} from '@angular/ssr/node';
import express, {response} from 'express';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';
import { Request, Response } from 'express';
import {environment} from "./src/environments/environment";
import crypto from "crypto";
import {getClientSitemapAsync} from "./src/server-request-sitemap";

// The Express app is exported so that it can be used by serverless Functions.
export function app(): express.Express {
  const server = express();
  const serverDistFolder = dirname(fileURLToPath(import.meta.url));
  const browserDistFolder = resolve(serverDistFolder, '../browser');

  const angularNodeAppEngine  = new AngularNodeAppEngine();

  server.set('view engine', 'html');
  server.set('views', browserDistFolder);

  const headerCspNonceValuePlaceholder = '{{{NH_CSP_NONCE_PLACEHOLDER}}}';
  let _cspHeaderValue: string|null = null;

  const getCspHeader = (nonce: string) => {
    if(!_cspHeaderValue) {
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
          //`'nonce-${headerCspNonceValuePlaceholder}'`,
          "'unsafe-inline'"
        ],
        fontSrc: [
          "'self'",
        ],
        connectSrc: [
          "'self'",
          "ws:",
        ],
        frameSrc: [
          "'self'",
        ]
      };

      cspHeaderData.connectSrc.push(environment.baseUrl);

      cspHeaderData.imageSrc.push(environment.baseUrl);

      const cspHeaderValues = [];
      cspHeaderValues.push(`default-src ${cspHeaderData.defaultSrc.join(' ').trim()}`);
      cspHeaderValues.push(`style-src ${cspHeaderData.styleSrc.join(' ').trim()}`);
      cspHeaderValues.push(`script-src ${cspHeaderData.scriptSrc.join(' ').trim()}`);
      cspHeaderValues.push(`font-src ${cspHeaderData.fontSrc.join(' ').trim()}`);
      cspHeaderValues.push(`connect-src ${cspHeaderData.connectSrc.join(' ').trim()}`);
      cspHeaderValues.push(`img-src ${cspHeaderData.imageSrc.join(' ').trim()}`);
      cspHeaderValues.push(`frame-src ${cspHeaderData.frameSrc.join(' ').trim()}`);

      _cspHeaderValue = cspHeaderValues.join('; ').trim();
    }

    return _cspHeaderValue?.replace(headerCspNonceValuePlaceholder, nonce) ?? '';
  };

  const setCspHeader = (res: any, nonce: string) => {
    const cspHeaderValue = getCspHeader(nonce);
    res.header('Content-Security-Policy', cspHeaderValue);
  };

  const setDefaultHeaders = (res: Response) => {
    res.header('Strict-Transport-Security', 'max-age=31536000');
    res.header('Cache-Control', 'no-cache, no-store, must-revalidate, pre-check=0, post-check=0, max-age=0, s-maxage=0');
    res.header('Pragma', 'no-cache');
    res.header('Expires', '0');
    res.header('X-Frame-Options', 'SAMEORIGIN');
    res.header('X-Content-Type-Options', 'nosniff');
    res.header('X-XSS-Protection', "'1; mode=block'");
    res.header('Referrer-Policy', 'strict-origin');
  };

  const getServerRequestContext = (req: Request, res: Response) => {
    const nonce = crypto.randomBytes(16).toString('base64');

    const requestContext: any = { server: 'express', appNonce: nonce, response: res, request: req };

    return requestContext;
  }

  // Example Express Rest API endpoints
  // server.get('/api/**', (req, res) => { });
  // Serve static files from /browser
  server.get('*.*', express.static(browserDistFolder, {
    maxAge: '1y'
  }));

  server.get('/sitemap.xml', (req, res, next) => {
    res.header('Content-Type', 'text/xml');

    const requestContext = getServerRequestContext(req, res);
    requestContext.__DO_GENERATE_SITEMAP_DATA__ = true;
    requestContext.__GENERATED_SITEMAP_DATA__ = null;

    angularNodeAppEngine
      .handle(req, requestContext)
      .then((response) => {
        // TODO: add some cache headers?
        const sitemapGeneratedDataObj = requestContext.__GENERATED_SITEMAP_DATA__;
        getClientSitemapAsync(sitemapGeneratedDataObj).then(sitemapXml => {
          res.send(sitemapXml);
        }).catch(next);
      })
      .catch(next);
  });

  // All regular routes use the Angular engine
  server.get('*', (req, res, next) => {
    const requestContext = getServerRequestContext(req, res);

    angularNodeAppEngine
      .handle(req, requestContext)
      .then((response) => {
        if(response?.status === 301) {
          res.statusCode = 301;
        }

        if(res.statusCode === 301) {
          res.send();
          return;
        }

        setDefaultHeaders(res);
        setCspHeader(res, requestContext.appNonce);

        response ? writeResponseToNodeResponse(response, res) : next()
      })
      .catch(next);
  });

  return server;
}

const server = app();
if(environment.name === 'development') {
  if (isMainModule(import.meta.url)) {
    const port = process.env['PORT'] || 4200;
    server.listen(port, () => {
      console.log(`Node Express server listening on http://localhost:\${port}`);
    });
  }
} else {
  const port = process.env['PORT'] || 4200;
  server.listen(port, () => {
    console.log(`Node Express server listening on http://localhost:${port}`);
  });
}

console.warn('Node Express server started');

export const reqHandler = createNodeRequestHandler(server);
