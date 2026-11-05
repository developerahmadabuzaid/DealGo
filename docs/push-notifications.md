# Push notifications (Android and iPhone)

The server already writes a notification for every event: auction starting, won, prize to pay,
order shipped, and so on. They appear in the app's notification list. Push makes the same
notification wake the phone. It is sent through Firebase Cloud Messaging (FCM): Android directly,
iPhone through Firebase's link to Apple.

Until the steps below are done, nothing changes for anyone. The app runs without push and the
in-app list keeps working.

## 1. Create the Firebase project (once)

1. Go to https://console.firebase.google.com and click **Add project**, for example "DealGo".
   Google Analytics is not needed.
2. **Add an Android app** with package name `com.dealgosa.app`. Skip downloading
   `google-services.json`; the app does not use it.
3. **Add an iOS app** with bundle ID `com.dealgosa.app`. Skip downloading
   `GoogleService-Info.plist` too.

## 2. Put the app's values in the code

Open **Project settings > General > Your apps**. Copy these values into
`mobile/lib/core/push/firebase_config.dart`:

| In firebase_config.dart | Where it is in Firebase |
|---|---|
| `projectId` | Project ID (top of the page) |
| `messagingSenderId` | Project number, also on the Cloud Messaging tab |
| `androidApiKey` | Android app's "Web API key" / `current_key`; the same key as the project's |
| `androidAppId` | Android app's App ID (`1:…:android:…`) |
| `iosApiKey` | iOS app's API key (often the same key as Android's) |
| `iosAppId` | iOS app's App ID (`1:…:ios:…`) |

These values are public by design. They are inside every copy of the app, so it is fine that the
repository is public.

## 3. Let Firebase reach iPhones (once)

1. Go to https://developer.apple.com/account. Under **Certificates, IDs & Profiles > Keys**,
   create a key with **Apple Push Notifications service (APNs)** ticked. Download the `.p8` file.
   Apple lets you download it only once, so keep it safe and **never commit it**.
2. Under **Identifiers**, open `com.dealgosa.app` and tick **Push Notifications**, then save.
3. In Firebase, go to **Project settings > Cloud Messaging > Apple app configuration** and upload
   the `.p8`, with its Key ID and your Team ID.

Codemagic makes a new provisioning profile on the next build. The App ID now has push, so the
profile includes it.

## 4. Let the server send (once)

1. In Firebase, go to **Project settings > Service accounts > Generate new private key**. A JSON
   file downloads. **It is a secret.** Keep it outside this repository, for example
   `C:\secrets\dealgo-firebase.json`.
2. Add this line to `deploy/smarterasp/local-secrets.ps1`:

   ```powershell
   $FirebaseServiceAccountPath = 'C:\secrets\dealgo-firebase.json'
   ```

3. Build the site package as usual. The script checks the file and writes it into `web.config` as
   `Firebase__CredentialsJson`. It prints "Push notifications are on".

## 5. Build and check

- Android: rebuild the APK/AAB.
- iPhone: push to GitHub and run the Codemagic build.
- Upload the new site package.

Sign in on a phone and allow notifications. Then do something that sends one, for example join an
auction that is about to start. Tapping the notification opens the auction (or the orders page,
for an order).

## How it works

- After sign-in the app asks for permission and gets this phone's FCM token. It sends the token to
  `POST /api/v1/notifications/devices`. On sign-out it removes the token again.
- When the server stores a notification, it also sends it to every token the person has. Firebase
  reports some tokens as gone (app removed, token replaced). Those are deleted.
- A push never decides anything about money. A failed push is only logged, and the notification is
  still in the in-app list.
- Only the server holds the service account. The app holds only the public project values above.
