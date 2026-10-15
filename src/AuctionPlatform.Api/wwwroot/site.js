/*
 * DealGo — the public landing page.
 *
 * The page has no words of its own. It asks the API what to say and builds itself from the answer,
 * so an administrator changes the front page from the console rather than through a deployment.
 *
 * Everything the API returns is treated as text, never as markup: a headline is written with
 * textContent and an address is checked before it becomes an href. The content is typed by a
 * trusted administrator, but "trusted" is not the same as "cannot make a mistake", and a page
 * that would execute whatever was pasted into a form is one bad paste away from doing it.
 */

'use strict';

const API = '/api/v1';

// ---------------------------------------------------------------- tiny DOM builder

/** Creates an element. Children that are strings become text nodes, never markup. */
function h(tag, attrs = {}, ...children) {
  const el = document.createElement(tag);

  for (const [k, v] of Object.entries(attrs)) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    else el.setAttribute(k, v);
  }

  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }

  return el;
}

/*
 * An address is safe to put in an href.
 *
 * Only http and https, plus a same-page anchor and a path on this site. Any other scheme is
 * dropped, which is what stops "javascript:" or a "data:" document typed into the console's link
 * box from becoming a working link here. An outside http(s) host is allowed on purpose: the
 * app-store buttons and the privacy policy all point off-site.
 */
function safeUrl(value) {
  const raw = (value || '').trim();
  if (!raw) return null;

  if (raw.startsWith('/') && !raw.startsWith('//')) return raw;
  if (raw.startsWith('#')) return raw;

  try {
    const url = new URL(raw, location.origin);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.href : null;
  } catch {
    return null;
  }
}

/** mailto: and tel: get their own check, since they are not web addresses. */
const mailTo = (v) => (v && v.includes('@') && !/\s/.test(v) ? `mailto:${v}` : null);
const telTo = (v) => (v && /^[+0-9()\-\s]{5,}$/.test(v) ? `tel:${v.replace(/\s/g, '')}` : null);

// ---------------------------------------------------------------- icons

/*
 * The icon set, drawn rather than loaded.
 *
 * The same names the admin console uses, so a card given the icon "gift" in the console shows a
 * gift here. An unknown name falls back to the spark, which is why a typo cannot leave a hole.
 */
const ICONS = {
  gavel: '<path d="M13.5 5.5 18.5 10.5"/><path d="m8 11 5-5 5 5-5 5z"/><path d="m4.5 19.5 6-6"/><path d="M3 21h7"/>',
  coins: '<ellipse cx="12" cy="6.5" rx="7" ry="3"/><path d="M5 6.5v5c0 1.7 3.1 3 7 3s7-1.3 7-3v-5"/><path d="M5 11.5v5c0 1.7 3.1 3 7 3s7-1.3 7-3v-5"/>',
  gift: '<rect x="3.5" y="9.5" width="17" height="11" rx="2"/><path d="M3.5 13.5h17"/><path d="M12 9.5v11"/><path d="M12 9.5S10 3.5 7.5 4.5 9.5 9.5 12 9.5s4.5-4 2-5-2 5-2 5z"/>',
  cart: '<circle cx="9" cy="20" r="1.4"/><circle cx="17.5" cy="20" r="1.4"/><path d="M2.5 3.5h2.6l2.3 11.2h10.6l2-8H6.4"/>',
  users: '<circle cx="9" cy="8" r="3.4"/><path d="M2.8 20a6.2 6.2 0 0 1 12.4 0"/><path d="M16.5 5.2a3.4 3.4 0 0 1 0 6.6"/><path d="M17.5 14.5a6.2 6.2 0 0 1 3.7 5.5"/>',
  clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7v5.4l3.4 2"/>',
  wallet: '<rect x="3" y="6" width="18" height="13" rx="3"/><path d="M3 10h18"/><circle cx="17" cy="14" r="1.2"/>',
  truck: '<rect x="2.5" y="7" width="11" height="9" rx="1.5"/><path d="M13.5 10h3.6l2.9 3v3h-6.5z"/><circle cx="7" cy="18" r="1.6"/><circle cx="17" cy="18" r="1.6"/>',
  shield: '<path d="M12 3 5 6v5.5c0 4.3 3 8 7 9.5 4-1.5 7-5.2 7-9.5V6z"/><path d="m9 12 2 2 4-4"/>',
  check: '<circle cx="12" cy="12" r="8.5"/><path d="m8.5 12 2.4 2.4 4.6-4.8"/>',
  search: '<circle cx="11" cy="11" r="6.5"/><path d="m16 16 4.5 4.5"/>',
  spark: '<path d="m12 3 2.2 5.8L20 11l-5.8 2.2L12 19l-2.2-5.8L4 11l5.8-2.2z"/>',
  phone: '<rect x="6.5" y="2.5" width="11" height="19" rx="2.5"/><path d="M10.5 5.5h3"/>',
  star: '<path d="m12 3 2.6 5.7 6.2.7-4.6 4.2 1.2 6.1L12 16.8 6.6 19.7l1.2-6.1L3.2 9.4l6.2-.7z"/>',
  chevron: '<path d="m6.5 9.5 5.5 5.5 5.5-5.5"/>',
};

function icon(name) {
  const span = h('span', { class: 'ico' });
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.innerHTML = ICONS[name] || ICONS.spark;
  span.append(svg);
  return span;
}

/** A filled star, for a review's rating. */
function starRow(count) {
  if (!count || count < 1) return null;

  const row = h('span', { class: 'stars', 'aria-label': `${count} من 5` });
  for (let i = 0; i < Math.min(5, count); i += 1) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.innerHTML = ICONS.star;
    row.append(svg);
  }
  return row;
}

// ---------------------------------------------------------------- the brand mark

const BRAND_MARK_SVG =
  '<g fill="#F26A1B">' +
  '<rect x="2" y="19" width="27" height="7" rx="3.5"/>' +
  '<rect x="0" y="30.5" width="19" height="7" rx="3.5"/>' +
  '<rect x="5" y="42" width="24" height="7" rx="3.5"/>' +
  '</g>' +
  '<rect x="16" y="30.5" width="42" height="7" rx="3.5" fill="#F26A1B"/>' +
  '<path d="M52 23.5 L70 34 L52 44.5 Z" fill="#F26A1B"/>' +
  '<path fill="#FFFFFF" fill-rule="evenodd" d="M42 5h20a29 29 0 0 1 0 58H42z' +
  'm13 13v32h7a16 16 0 0 0 0-32z"/>' +
  '<path fill="#F26A1B" fill-rule="evenodd" d="M126 5a29 29 0 1 0 0 58 29 29 0 0 0 0-58z' +
  'm0 13a16 16 0 1 1 0 32 16 16 0 0 1 0-32z"/>' +
  '<rect x="126" y="28" width="29" height="12" fill="#F26A1B"/>';

/** The logo file when the server has one, the drawn mark when it does not. */
function brandMark(box) {
  const drawn = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  drawn.setAttribute('viewBox', '0 0 162 68');
  drawn.setAttribute('aria-hidden', 'true');
  drawn.innerHTML = BRAND_MARK_SVG;

  const img = h('img', { src: '/brand/logo.png', alt: '' });
  img.addEventListener('error', () => box.replaceChildren(drawn));
  box.replaceChildren(img);
}

// ---------------------------------------------------------------- sections

const T = (v) => (v || '').trim();
const has = (v) => T(v).length > 0;

/** The pill, the heading and the sentence that open a section. */
function sectionHead(eyebrow, title, subtitle) {
  if (!has(eyebrow) && !has(title) && !has(subtitle)) return null;

  return h('div', { class: 'section-head' },
    has(eyebrow) ? h('span', { class: 'eyebrow' }, h('span', { class: 'dot' }), T(eyebrow)) : null,
    has(title) ? h('h2', {}, T(title)) : null,
    has(subtitle) ? h('p', {}, T(subtitle)) : null);
}

/** A section wrapper that reveals itself once as it scrolls into view. */
function section(id, ...children) {
  const el = h('section', { id, class: 'reveal' }, h('div', { class: 'wrap' }, ...children));
  revealed.push(el);
  return el;
}

const revealed = [];

const APP_STORE_SVG =
  '<path d="M17.05 12.5c0-2.1 1.7-3.1 1.8-3.2-1-1.4-2.5-1.6-3-1.7-1.3-.1-2.5.8-3.1.8s-1.6-.7-2.7-.7c-1.4 0-2.6.8-3.3 2-1.4 2.4-.4 6 1 8 .7 1 1.5 2 2.5 2 1 0 1.4-.6 2.6-.6s1.6.6 2.7.6 1.8-1 2.4-2c.8-1.1 1.1-2.2 1.1-2.3 0 0-2-.8-2-3zM15 6.2c.5-.7.9-1.6.8-2.6-.8 0-1.8.6-2.4 1.3-.5.6-1 1.6-.8 2.5.9.1 1.8-.5 2.4-1.2z"/>';

const PLAY_SVG =
  '<path d="M3.6 2.3c-.3.3-.5.8-.5 1.4v16.6c0 .6.2 1.1.5 1.4l.1.1 9.3-9.3v-.2L3.7 2.2z"/>' +
  '<path d="m16.1 15.6-3.1-3.1v-.2l3.1-3.1.1.1 3.7 2.1c1 .6 1 1.6 0 2.2z"/>' +
  '<path d="m16.2 15.5-3.2-3.2-9.4 9.4c.4.4 1 .4 1.6.1z"/>' +
  '<path d="M16.2 8.5 5.2 2.3c-.6-.4-1.2-.3-1.6.1l9.4 9.3z"/>';

/** The two app-store buttons, each shown only when its address exists. */
function storeRow(s) {
  const apple = safeUrl(s.appStoreUrl);
  const play = safeUrl(s.googlePlayUrl);
  if (!apple && !play) return null;

  const wrap = h('div', { class: 'stores' });
  if (apple) wrap.append(iconButton(apple, APP_STORE_SVG, 'متوفر على', 'App Store'));
  if (play) wrap.append(iconButton(play, PLAY_SVG, 'احصل عليه من', 'Google Play'));
  return wrap;

  function iconButton(href, paths, small, big) {
    const a = h('a', { class: 'store-btn', href, target: '_blank', rel: 'noopener noreferrer' });
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    svg.innerHTML = paths;
    a.append(svg, h('span', { class: 'store-text' }, h('small', {}, small), h('strong', {}, big)));
    return a;
  }
}

function heroSection(s) {
  const ctaHref = safeUrl(s.heroCtaUrl) || '#download';
  const shot = safeUrl(s.heroImageUrl);

  return section('top',
    h('div', { class: 'hero' },
      has(s.heroBadge)
        ? h('span', { class: 'eyebrow' }, h('span', { class: 'dot' }), T(s.heroBadge))
        : null,
      h('h1', {}, T(s.heroTitle)),
      has(s.heroSubtitle) ? h('p', { class: 'lede' }, T(s.heroSubtitle)) : null,
      has(s.heroCtaLabel)
        ? h('div', { class: 'hero-actions' },
            h('a', { class: 'btn btn-solid', href: ctaHref }, T(s.heroCtaLabel)),
            h('a', { class: 'btn btn-ghost', href: '#how' }, 'كيف تعمل؟'))
        : null,
      storeRow(s),
      shot ? h('div', { class: 'hero-shot' }, h('img', { src: shot, alt: '', loading: 'lazy' })) : null));
}

function aboutSection(s) {
  if (!s.aboutEnabled) return null;
  if (!has(s.featuresBody) && !has(s.visionBody)) return null;

  const card = (iconName, title, body) => has(title) || has(body)
    ? h('article', { class: 'card' },
        h('div', { class: 'card-icon' }, icon(iconName)),
        has(title) ? h('h3', {}, T(title)) : null,
        has(body) ? h('p', {}, T(body)) : null)
    : null;

  return section('about',
    sectionHead(s.aboutEyebrow, s.aboutTitle, null),
    h('div', { class: 'grid two' },
      card('spark', s.featuresTitle, s.featuresBody),
      card('shield', s.visionTitle, s.visionBody)));
}

/** A count, written the way a marketing figure is: 70k rather than 70,000. */
function shortNumber(n) {
  const value = Number(n) || 0;
  if (value >= 1000000) return `${(value / 1000000).toFixed(value % 1000000 === 0 ? 0 : 1)}M+`;
  if (value >= 1000) return `${(value / 1000).toFixed(value % 1000 === 0 ? 0 : 1)}k+`;
  return String(value);
}

function statsSection(s, stats) {
  if (!s.statsEnabled) return null;

  const one = (value, label) => has(label)
    ? h('div', { class: 'stat' },
        h('div', { class: 'n' }, shortNumber(value)),
        h('div', { class: 'l' }, T(label)))
    : null;

  const cells = [
    one(stats.users, s.statUsersLabel),
    one(stats.auctions, s.statAuctionsLabel),
    one(stats.products, s.statProductsLabel),
  ].filter(Boolean);

  if (!cells.length) return null;

  return section('stats', h('div', { class: 'stats' }, ...cells));
}

function cardsSection(id, enabled, eyebrow, title, subtitle, cards, numbered) {
  if (!enabled || !cards.length) return null;

  const columns = cards.length >= 4 ? 'four' : cards.length === 3 ? 'three' : 'two';

  return section(id,
    sectionHead(eyebrow, title, subtitle),
    h('div', { class: `grid ${columns}` },
      ...cards.map((c, i) => {
        const picture = safeUrl(c.imageUrl);

        return h('article', { class: 'card' },
          numbered && !picture
            ? h('span', { class: 'step-no', 'aria-hidden': 'true' }, String(i + 1))
            : null,
          // A picture when one was uploaded, the icon otherwise. Never both: side by side they
          // read as two competing marks for the same card.
          picture
            ? h('div', { class: 'card-shot' }, h('img', { src: picture, alt: '', loading: 'lazy' }))
            : h('div', { class: 'card-icon' }, icon(c.icon)),
          h('h3', {}, T(c.title)),
          has(c.body) ? h('p', {}, T(c.body)) : null);
      })));
}

/*
 * The picture row.
 *
 * Each image with its own words underneath, which is the whole point of the section: the caption
 * belongs to the picture above it rather than to the row. Three across on a wide screen, and one
 * under another on a phone, so a caption is never separated from what it describes.
 */
function showcaseSection(s, cards) {
  if (!s.showcaseEnabled || !cards.length) return null;

  const shown = cards.filter((c) => safeUrl(c.imageUrl));
  if (!shown.length) return null;

  const columns = shown.length >= 3 ? 'three' : shown.length === 2 ? 'two' : 'one';

  return section('showcase',
    sectionHead(s.showcaseEyebrow, s.showcaseTitle, s.showcaseSubtitle),
    h('div', { class: `grid ${columns} shots` },
      ...shown.map((c) => h('figure', { class: 'shot' },
        h('div', { class: 'shot-frame' },
          h('img', { src: safeUrl(c.imageUrl), alt: T(c.title), loading: 'lazy' })),
        h('figcaption', { class: 'shot-caption' },
          h('h3', {}, T(c.title)),
          has(c.body) ? h('p', {}, T(c.body)) : null)))));
}

function reviewsSection(s, reviews) {
  if (!s.testimonialsEnabled || !reviews.length) return null;

  return section('reviews',
    sectionHead(s.testimonialsEyebrow, s.testimonialsTitle, s.testimonialsSubtitle),
    h('div', { class: 'reviews' },
      ...reviews.map((r) => {
        const avatarSrc = safeUrl(r.avatarUrl);

        // A picture when there is one, otherwise the first letter of the name.
        const avatar = avatarSrc
          ? h('img', { class: 'review-avatar', src: avatarSrc, alt: '', loading: 'lazy' })
          : h('span', { class: 'review-avatar', 'aria-hidden': 'true' }, T(r.name).charAt(0));

        return h('article', { class: 'card review' },
          starRow(r.rating),
          h('p', { class: 'review-body' }, T(r.body)),
          h('div', { class: 'review-who' },
            avatar,
            h('div', {},
              h('div', { class: 'review-name' }, T(r.name)),
              has(r.role) ? h('div', { class: 'review-role' }, T(r.role)) : null)));
      })));
}

function faqSection(s, faqs) {
  if (!s.faqEnabled || !faqs.length) return null;

  return section('faq',
    sectionHead(s.faqEyebrow, s.faqTitle, s.faqSubtitle),
    h('div', { class: 'faq' },
      ...faqs.map((f) => h('details', { class: 'qa' },
        h('summary', {}, T(f.question), h('span', { class: 'chev' }, icon('chevron'))),
        h('p', { class: 'answer' }, T(f.answer))))));
}

function downloadSection(s) {
  if (!s.downloadEnabled) return null;
  if (!has(s.downloadTitle) && !safeUrl(s.appStoreUrl) && !safeUrl(s.googlePlayUrl)) return null;

  return section('download',
    h('div', { class: 'cta-band' },
      has(s.downloadTitle) ? h('h2', {}, T(s.downloadTitle)) : null,
      has(s.downloadSubtitle) ? h('p', {}, T(s.downloadSubtitle)) : null,
      storeRow(s)));
}

// ---------------------------------------------------------------- footer

const SOCIAL_SVG = {
  twitter: '<path d="M18.2 2.3h3.3l-7.2 8.2 8.5 11.2h-6.7l-5.2-6.8-6 6.8H1.6l7.7-8.8L1.2 2.3H8l4.7 6.2zm-1.2 17.7h1.8L7.1 4.2H5.1z"/>',
  instagram: '<path d="M12 2.2c3.2 0 3.6 0 4.9.1 1.2.1 1.8.2 2.2.4.6.2 1 .5 1.4.9s.7.8.9 1.4c.2.4.4 1 .4 2.2.1 1.3.1 1.7.1 4.9s0 3.6-.1 4.9c-.1 1.2-.2 1.8-.4 2.2-.2.6-.5 1-.9 1.4s-.8.7-1.4.9c-.4.2-1 .4-2.2.4-1.3.1-1.7.1-4.9.1s-3.6 0-4.9-.1c-1.2-.1-1.8-.2-2.2-.4-.6-.2-1-.5-1.4-.9s-.7-.8-.9-1.4c-.2-.4-.4-1-.4-2.2C2.2 15.6 2.2 15.2 2.2 12s0-3.6.1-4.9c.1-1.2.2-1.8.4-2.2.2-.6.5-1 .9-1.4s.8-.7 1.4-.9c.4-.2 1-.4 2.2-.4C8.4 2.2 8.8 2.2 12 2.2zm0 3.1A6.7 6.7 0 1 0 18.7 12 6.7 6.7 0 0 0 12 5.3zm0 11A4.3 4.3 0 1 1 16.3 12 4.3 4.3 0 0 1 12 16.3zm6.9-11.2a1.6 1.6 0 1 1-1.6-1.6 1.6 1.6 0 0 1 1.6 1.6z"/>',
  tiktok: '<path d="M16.6 2h3.1a5.7 5.7 0 0 0 4.1 4.3v3.2a9 9 0 0 1-4.1-1.2v6.9a6.6 6.6 0 1 1-6.6-6.6c.3 0 .6 0 .9.1v3.3a3.4 3.4 0 1 0 2.4 3.2z"/>',
  snapchat: '<path d="M12 2c2.8 0 4.8 2 4.9 4.8 0 .8-.1 1.6-.1 2.2.4.2.9.1 1.3-.1.6-.2 1.2.5.8 1-.3.4-1 .8-1.7 1-.3.1-.4.3-.3.6.5 1.5 1.8 2.9 3.3 3.3.6.2.5 1-.1 1.1-.9.2-1.5.3-1.8.6-.2.2-.1.6-.4.8-.4.3-1.2 0-2 .1-1 .1-1.8 1.4-3.9 1.4s-2.9-1.3-3.9-1.4c-.8-.1-1.6.2-2-.1-.3-.2-.2-.6-.4-.8-.3-.3-.9-.4-1.8-.6-.6-.1-.7-.9-.1-1.1 1.5-.4 2.8-1.8 3.3-3.3.1-.3 0-.5-.3-.6-.7-.2-1.4-.6-1.7-1-.4-.5.2-1.2.8-1 .4.2.9.3 1.3.1 0-.6-.1-1.4-.1-2.2C7.2 4 9.2 2 12 2z"/>',
  whatsapp: '<path d="M12.05 2A9.9 9.9 0 0 0 3.6 17.1L2.3 22l5-1.3A9.9 9.9 0 1 0 12.05 2zm0 18.1a8.2 8.2 0 0 1-4.2-1.2l-.3-.2-3 .8.8-2.9-.2-.3a8.2 8.2 0 1 1 6.9 3.8zm4.5-6.1c-.2-.1-1.5-.7-1.7-.8s-.4-.1-.6.1-.6.8-.8 1-.3.2-.6.1a6.7 6.7 0 0 1-3.3-2.9c-.2-.4.2-.4.6-1.2a.6.6 0 0 0 0-.6c0-.1-.6-1.4-.8-1.9s-.4-.4-.6-.4h-.5a1 1 0 0 0-.7.3 3 3 0 0 0-.9 2.2 5.2 5.2 0 0 0 1.1 2.7 11.8 11.8 0 0 0 4.5 4 7.6 7.6 0 0 0 3.1.7 2.7 2.7 0 0 0 1.8-1.3 2.2 2.2 0 0 0 .2-1.3c-.1-.2-.3-.3-.5-.4z"/>',
};

function socialLink(name, url) {
  const href = safeUrl(url);
  if (!href) return null;

  const a = h('a', {
    class: 'social', href, target: '_blank', rel: 'noopener noreferrer', 'aria-label': name,
  });
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  svg.innerHTML = SOCIAL_SVG[name] || '';
  a.append(svg);
  return a;
}

function buildFooter(s, links) {
  const socials = [
    socialLink('twitter', s.twitterUrl),
    socialLink('instagram', s.instagramUrl),
    socialLink('tiktok', s.tiktokUrl),
    socialLink('snapchat', s.snapchatUrl),
    socialLink('whatsapp', s.whatsappUrl),
  ].filter(Boolean);

  // The site ships its own policy pages; an address set in the console replaces them.
  const legal = [
    h('a', { href: safeUrl(s.privacyUrl) || '/privacy.html' }, 'سياسة الخصوصية'),
    h('a', { href: safeUrl(s.termsUrl) || '/terms.html' }, 'الشروط والأحكام'),
  ];

  const contact = [
    mailTo(s.contactEmail) ? h('a', { href: mailTo(s.contactEmail) }, T(s.contactEmail)) : null,
    telTo(s.contactPhone) ? h('a', { href: telTo(s.contactPhone), dir: 'ltr' }, T(s.contactPhone)) : null,
  ].filter(Boolean);

  const mark = h('span', { class: 'brand-mark' });
  brandMark(mark);

  const columns = h('div', { class: 'footer-links' });

  if (links.length) {
    columns.append(h('div', { class: 'footer-col' },
      h('h4', {}, 'الصفحة'),
      ...links.map((l) => h('a', { href: `#${l.id}` }, l.label))));
  }
  if (legal.length) {
    columns.append(h('div', { class: 'footer-col' }, h('h4', {}, 'قانوني'), ...legal));
  }
  if (contact.length) {
    columns.append(h('div', { class: 'footer-col' }, h('h4', {}, 'تواصل معنا'), ...contact));
  }

  return h('div', { class: 'wrap' },
    h('div', { class: 'footer-top' },
      h('div', { class: 'footer-brand' },
        h('a', { class: 'brand', href: '#top' },
          mark,
          h('span', { class: 'brand-word' }, h('b', {}, 'Deal'), h('i', {}, 'Go'))),
        has(s.footerNote) ? h('p', {}, T(s.footerNote)) : null,
        socials.length ? h('div', { class: 'socials' }, ...socials) : null),
      columns),
    h('div', { class: 'footer-base' },
      h('span', {}, `© ${new Date().getFullYear()} DealGo`),
      h('span', {}, 'صفقتك جاهزة')));
}

// ---------------------------------------------------------------- assembly

function render(data) {
  const s = data.settings;
  const page = document.getElementById('page');

  document.title = has(s.metaTitle) ? T(s.metaTitle) : document.title;
  if (has(s.metaDescription)) {
    const tag = document.querySelector('meta[name="description"]');
    if (tag) tag.setAttribute('content', T(s.metaDescription));
  }

  revealed.length = 0;

  const sections = [
    heroSection(s),
    statsSection(s, data.stats),
    aboutSection(s),
    cardsSection('how', s.stepsEnabled, s.stepsEyebrow, s.stepsTitle, s.stepsSubtitle,
      data.steps, true),
    cardsSection('take-part', s.participateEnabled, s.participateEyebrow, s.participateTitle,
      s.participateSubtitle, data.participate, false),
    showcaseSection(s, data.showcase),
    reviewsSection(s, data.testimonials),
    faqSection(s, data.faqs),
    downloadSection(s),
  ].filter(Boolean);

  page.className = '';
  page.removeAttribute('aria-busy');
  page.replaceChildren(...sections);

  // The header links name the sections that actually exist, so none of them can lead nowhere.
  const labels = {
    about: 'من نحن', how: 'كيف تعمل', 'take-part': 'المشاركة', showcase: 'الصور',
    reviews: 'الآراء', faq: 'الأسئلة', download: 'التطبيق',
  };
  const links = sections
    .map((el) => ({ id: el.id, label: labels[el.id] }))
    .filter((l) => l.label);

  const nav = document.getElementById('topnav');
  nav.replaceChildren(...links.map((l) => h('a', { href: `#${l.id}` }, l.label)));

  const cta = document.getElementById('topCta');
  if (has(s.heroCtaLabel)) cta.textContent = T(s.heroCtaLabel);
  cta.setAttribute('href', safeUrl(s.heroCtaUrl) || '#download');

  document.getElementById('footer').replaceChildren(buildFooter(s, links));

  watchSections(links.map((l) => l.id));
  revealOnScroll();
}

/** Reveals each section once, the first time it comes into view. */
function revealOnScroll() {
  if (!('IntersectionObserver' in window)) {
    revealed.forEach((el) => el.classList.add('shown'));
    return;
  }

  const io = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      entry.target.classList.add('shown');
      io.unobserve(entry.target);
    }
  }, { rootMargin: '0px 0px -12% 0px' });

  revealed.forEach((el) => io.observe(el));
}

/** Marks the header link for whichever section is on screen. */
function watchSections(ids) {
  if (!('IntersectionObserver' in window) || !ids.length) return;

  const io = new IntersectionObserver((entries) => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      document.querySelectorAll('.topnav a').forEach((a) => {
        a.classList.toggle('current', a.getAttribute('href') === `#${entry.target.id}`);
      });
    }
  }, { rootMargin: '-45% 0px -50% 0px' });

  ids.forEach((id) => {
    const el = document.getElementById(id);
    if (el) io.observe(el);
  });
}

// ---------------------------------------------------------------- boot

function wireChrome() {
  brandMark(document.getElementById('brandMark'));

  const bar = document.getElementById('topbar');
  const onScroll = () => bar.classList.toggle('stuck', window.scrollY > 8);
  window.addEventListener('scroll', onScroll, { passive: true });
  onScroll();

  const btn = document.getElementById('menuBtn');
  const nav = document.getElementById('topnav');

  btn.addEventListener('click', () => {
    const open = nav.classList.toggle('open');
    btn.setAttribute('aria-expanded', open ? 'true' : 'false');
  });

  // Following a link closes the sheet; leaving it open hides the section just jumped to.
  nav.addEventListener('click', (e) => {
    if (e.target.tagName !== 'A') return;
    nav.classList.remove('open');
    btn.setAttribute('aria-expanded', 'false');
  });
}

async function boot() {
  wireChrome();

  try {
    const res = await fetch(`${API}/site/content`, { headers: { Accept: 'application/json' } });
    if (!res.ok) throw new Error(String(res.status));
    render(await res.json());
  } catch {
    // Says what happened rather than leaving the shimmer running for ever.
    document.getElementById('page').replaceChildren(
      h('div', { class: 'error-note' },
        h('p', {}, 'تعذّر تحميل محتوى الصفحة.'),
        h('a', { class: 'btn btn-ghost sm', href: location.pathname }, 'إعادة المحاولة')));
  }
}

boot();
