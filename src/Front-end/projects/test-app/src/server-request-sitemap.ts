import { AxiosResponse } from 'axios';
import {environment} from "./environments/environment";
import * as https from "node:https";
import {NhSitemap} from "nh-common";
const axios = require('axios');

const httpsAgent = new https.Agent({
  rejectUnauthorized: false, // (NOTE: this will disable client verification)
});

export const getBaseSitemapAsync = async () => {
  let baseUrl = environment.baseUrl;

  // If base url end with /, remove it
  if (baseUrl.endsWith('/')) {
    baseUrl = baseUrl.slice(0, -1);
  }

  let sitemapString = ``;
  const writeSitemapLine = (line: string) => {
    sitemapString += `${line}\n`;
  };

  writeSitemapLine(`<?xml version="1.0" encoding="UTF-8"?>`);
  writeSitemapLine(`<sitemapindex xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">`);
  writeSitemapLine(`    <sitemap>`);
  writeSitemapLine(`        <loc>${baseUrl}/page-sitemap.xml</loc>`);
  writeSitemapLine(`    </sitemap>`);
  writeSitemapLine(`    <sitemap>`);
  writeSitemapLine(`        <loc>${baseUrl}/shop-sitemap.xml</loc>`);
  writeSitemapLine(`    </sitemap>`);
  writeSitemapLine(`</sitemapindex>`);
  writeSitemapLine(`</urlset>`);

  return sitemapString;
};

export const getClientSitemapAsync = async (clientSitemap: NhSitemap) => {
  let apiBaseUrl = environment.apiBaseUrl;
  let baseUrl = environment.baseUrl;

  // If base url end with /, remove it
  if (baseUrl.endsWith('/')) {
    baseUrl = baseUrl.slice(0, -1);
  }

  let sitemapString = ``;

  const writeSitemapLine = (line: string) => {
    sitemapString += `${line}\n`;
  };

  writeSitemapLine(`<?xml version="1.0" encoding="UTF-8"?>`);
  writeSitemapLine(`<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9" xmlns:xhtml="http://www.w3.org/1999/xhtml">`);

  for(const sitemapEntry of clientSitemap.entries) {
    const primaryItem = sitemapEntry.items.find(x => x.isPrimary);
    let primaryItemPath = primaryItem?.path;
    if (!primaryItemPath) {
      continue;
    }

    primaryItemPath = `${baseUrl}${primaryItemPath}`;

    if(primaryItemPath.endsWith('/')) {
      primaryItemPath = primaryItemPath.slice(0, -1);
    }

    writeSitemapLine(`    <url>`);
    writeSitemapLine(`        <loc>${primaryItemPath}</loc>`);
    for(const sitemapItem of sitemapEntry.items) {
      let sitemapItemPath = sitemapItem?.path;
      if (!sitemapItemPath) {
        continue;
      }

      sitemapItemPath = `${baseUrl}${sitemapItemPath}`;

      if(sitemapItemPath.endsWith('/')) {
        sitemapItemPath = sitemapItemPath.slice(0, -1);
      }

      writeSitemapLine(`        <xhtml:link hreflang="${sitemapItem.language}" href="${sitemapItemPath}" rel="alternate"/>`);
    }
    writeSitemapLine(`    </url>`);
  }

  writeSitemapLine(`</urlset>`);

  return sitemapString;
};

// const response = await axios.get(`${apiBaseUrl}/sitemap`, { httpsAgent });
//
// if (response.status !== 200) {
//   throw new Error(`Failed to fetch sitemap: ${response}`);
// }


