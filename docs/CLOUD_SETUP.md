# Cloud storage setup

Recital syncs through storage the user already has: a local folder kept in step by a desktop client, a
WebDAV server, Google Drive, Dropbox or OneDrive (plan section 2). WebDAV and local folders need nothing
from the build. The three cloud services need the app to be **registered** with each of them, and the
registration's public identifiers built in. They are not secrets: anyone can read them out of an
installed app, and every flow uses PKCE.

A build without an identifier still runs. That service's item in the library's Sync menu is disabled,
with a tooltip saying so.

## Building with the identifiers

```bash
dotnet build hosts/Majorsilence.Recital.Desktop -c Release \
  -p:RecitalGoogleClientId=1234-abc.apps.googleusercontent.com \
  -p:RecitalGoogleClientSecret=GOCSPX-... \
  -p:RecitalDropboxAppKey=abcd1234efgh567 \
  -p:RecitalOneDriveClientId=00000000-0000-0000-0000-000000000000
```

At run time, the environment variables `RECITAL_GOOGLE_CLIENT_ID`, `RECITAL_GOOGLE_CLIENT_SECRET`,
`RECITAL_DROPBOX_APP_KEY` and `RECITAL_ONEDRIVE_CLIENT_ID` override the built-in values.

## Google Drive

1. In the [Google Cloud console](https://console.cloud.google.com/), create a project and enable the
   **Google Drive API**.
2. Set up the **OAuth consent screen** (External). Add the scope `.../auth/drive.file`. It is
   non-sensitive: the app sees only files it created or the user opened with it, so no security review
   is needed (plan 2.5).
3. Under **Credentials**, create an **OAuth client ID** of type **Desktop app**. Copy the client ID and
   the client secret. Google requires the secret at the token endpoint for desktop clients and documents
   that it is not confidential there. It is never sent from the browser build.
4. Redirect: desktop clients accept `http://127.0.0.1:<any port>/`, so nothing needs registering.

While the consent screen is in "Testing", add the accounts that may sign in as test users.

## Dropbox

1. In the [App Console](https://www.dropbox.com/developers/apps), create an app: **Scoped access**,
   **Full Dropbox**. The sync home is the visible `/Recital` folder, and import can browse the whole
   Dropbox.
2. Permissions: `files.metadata.read`, `files.content.read`, `files.content.write`, `account_info.read`.
3. OAuth 2 redirect URIs: add `http://localhost:47612/`. Dropbox matches the redirect exactly, so
   Recital always uses that port for Dropbox sign-in.
4. Copy the **App key**. No app secret is used: PKCE replaces it.

A new app allows up to 500 users until it is approved for production.

## OneDrive (Microsoft Graph)

1. In the [Microsoft Entra admin center](https://entra.microsoft.com/), open **App registrations** and
   choose **New registration**. For "Supported account types" choose "Accounts in any organizational
   directory and personal Microsoft accounts".
2. Under **Authentication**, add a platform **Mobile and desktop applications** with the redirect URI
   `http://localhost`. The loopback port is ignored, so any port works. Turn on **Allow public client
   flows**.
3. Under **API permissions**, add the delegated Microsoft Graph permissions `Files.ReadWrite`,
   `offline_access` and `User.Read`.
4. Copy the **Application (client) ID**.

## Tests against the real services

The provider conformance suite (`tests/Majorsilence.Recital.Storage.Tests`) runs against fakes of each
API in normal CI. It also runs against the real services when these variables are set, working in a new
`recital-tests/<guid>` folder each time:

| Service | Variables |
|---|---|
| WebDAV | `RECITAL_TEST_WEBDAV_URL`, `_USER`, `_PASSWORD` (CI runs Apache mod_dav: `tests/webdav/run.sh`) |
| Dropbox | `RECITAL_DROPBOX_APP_KEY`, `RECITAL_TEST_DROPBOX_REFRESH_TOKEN` |
| OneDrive | `RECITAL_ONEDRIVE_CLIENT_ID`, `RECITAL_TEST_ONEDRIVE_REFRESH_TOKEN` |
| Google Drive | `RECITAL_GOOGLE_CLIENT_ID`, `RECITAL_GOOGLE_CLIENT_SECRET`, `RECITAL_TEST_GDRIVE_REFRESH_TOKEN` |

To get a refresh token, sign in to a test account once in the desktop app (Library > Sync > the
service), then read it from the OS secure store, where Recital keeps it under the key
`oauth:<service>` (`oauth:dropbox`, `oauth:onedrive`, `oauth:googledrive`):

- Linux: `secret-tool lookup key oauth:dropbox`
- macOS: `security find-generic-password -s Recital -a oauth:dropbox -w`
- Windows: Credential Manager, generic credential `Recital:oauth:dropbox`

Use a test account, not your own: the tests write into it.
