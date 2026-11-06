# Uploaded pictures on Cloudflare R2

Auction, prize, product, ad and showcase pictures uploaded from the admin console are stored by
`IMediaStorage`. With no R2 settings the API keeps them in `wwwroot/uploads` on the host, as it
always has. With the five R2 settings below it writes them to a Cloudflare R2 bucket and stores the
picture's public R2 address (for example `https://images.dealgosa.com/uploads/<guid>.jpg`) in the
database, which the app, the site and the console use as it is.

## 1. Create the bucket

1. Cloudflare dashboard → **R2 Object Storage** → **Create bucket**.
2. Name it, for example `dealgo-uploads`. Location: Automatic. Storage class: Standard.
3. On the R2 overview page, copy the **Account ID** (right-hand side).

## 2. Make it publicly readable

Pick one, in the bucket's **Settings** tab:

- **Custom domain (recommended).** Under *Custom Domains* → **Connect Domain**, enter
  `images.dealgosa.com`. This needs `dealgosa.com` to be a zone in the same Cloudflare account;
  Cloudflare adds the DNS record and certificate itself. Pictures are then cached by Cloudflare's CDN.
- **r2.dev address.** Under *Public Development URL* → **Allow Access**. You get
  `https://pub-<id>.r2.dev`. Cloudflare rate-limits this address and says it is not for production,
  so use it only until the custom domain is in place.

## 3. Allow the web app to read pictures (CORS)

The Flutter web app (dealgosa.com/app) downloads picture bytes itself, which a browser only allows
when the bucket says so. In the bucket's **Settings** → **CORS Policy** → **Add CORS policy**:

```json
[
  {
    "AllowedOrigins": ["https://dealgosa.com", "https://www.dealgosa.com"],
    "AllowedMethods": ["GET", "HEAD"],
    "AllowedHeaders": ["*"],
    "MaxAgeSeconds": 86400
  }
]
```

The Android and iPhone apps and the admin console do not need this, but the web app shows the
"no image" placeholder without it.

## 4. Create an API token for R2

1. R2 overview → **Manage API tokens** (under *Account details*) → **Create API token**
   (the "User API token" or "Account API token" kind both work).
2. Permissions: **Object Read & Write**. Under *Specify bucket(s)*, choose only `dealgo-uploads`.
3. TTL: Forever (or set a reminder to rotate it).
4. **Create API Token**, then copy **Access Key ID** and **Secret Access Key**. The secret is shown
   only once; if you lose it, roll a new token.

## 5. Where each value goes

Add these to `deploy/smarterasp/local-secrets.ps1` (git-ignored; never commit it):

```powershell
$R2AccountId       = '<Account ID from step 1>'
$R2AccessKeyId     = '<Access Key ID from step 4>'
$R2SecretAccessKey = '<Secret Access Key from step 4>'
$R2Bucket          = 'dealgo-uploads'
$R2PublicBaseUrl   = 'https://images.dealgosa.com'   # or the r2.dev address from step 2
```

Then rebuild the package:

```powershell
pwsh deploy/smarterasp/publish-smarterasp.ps1 -Zip
```

The script writes them into `web.config` as `Storage__Provider=R2` and `Storage__R2__AccountId`,
`__AccessKeyId`, `__SecretAccessKey`, `__Bucket`, `__PublicBaseUrl`. It refuses to build when only
some are set, and warns when none are. Upload the package as usual; new uploads go to R2 from then on.

For local development the same settings can be given as environment variables or user secrets
(`Storage:Provider=R2`, `Storage:R2:…`). With `Storage:Provider=R2` set and a value missing, the API
refuses to start and names the missing setting.

## 6. Move the existing pictures (one-off)

Before the switch, pictures lived on the host in two folders, and the database points at them by
path:

- `wwwroot/uploads`: everything administrators uploaded (`/uploads/…`).
- `wwwroot/catalog`: the built-in store catalog shipped with the site (`/catalog/…`).

To move all of them to R2, after the R2 build is uploaded and running:

1. Sign in to the admin console as a super administrator.
2. Open the browser's developer console (F12 → Console) on the admin page and run:

   ```js
   const token = JSON.parse(localStorage.getItem('auction.admin.auth')).accessToken;
   await (await fetch('/api/v1/admin/uploads/move-to-r2', {
     method: 'POST', headers: { Authorization: `Bearer ${token}` } })).json();
   ```
3. The reply gives `copied`, `alreadyThere`, `skipped` (files that are not pictures), `failed`, and
   `addressesUpdated`. If anything failed, run it again: it only does what is still left.

What it does:

- Copies each picture to the same path in the bucket (`uploads/x.jpg`, `catalog/x.webp`).
- Rewrites the database addresses for those pictures to `<R2PublicBaseUrl>/uploads/x.jpg` and so on,
  in products and their galleries, store product images, departments, auctions and prizes, ads,
  the site's showcase, and the picture saved on past orders. Only pictures confirmed in the bucket
  are rewritten; an address with its own host (a picture on another site) is never touched.
- Clears the store's cached listings, so the new addresses show at once.
- Deletes nothing from the host. The files stay there, and any `/uploads/…` or `/catalog/…`
  address that was kept somewhere is redirected to the bucket if its file goes.

Products the built-in catalog adds in a later version start with a `/catalog/…` address (the file
ships with the site, so it works). Run the move again after such an update to put them in R2 too.

To undo the address change, strip `<R2PublicBaseUrl>` from the start of those addresses; the same
paths still work on the host while the files are there.

## Switching back

Remove the R2 values from `local-secrets.ps1` and rebuild. New uploads go to `wwwroot/uploads`
again; pictures already stored with an R2 address keep loading from R2.
