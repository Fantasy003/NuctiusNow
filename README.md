# NuctiusNow

Blazor Server application targeting .NET 8, with PostgreSQL, SearXNG and a local LM Studio-compatible server.

## Run locally

Use the .NET 8 SDK and PostgreSQL. From the repository root:

```bash
dotnet restore NuctiusNow.sln
cd NuctiusNow/NuctiusNow
dotnet tool restore
# Configure ConnectionStrings__PostgresConnection securely for your local database.
dotnet ef database update
dotnet run --no-launch-profile --urls http://localhost:5217
```

Set `ASPNETCORE_ENVIRONMENT=Development` for local development. The included tool manifest pins Entity Framework tooling.

The migrations now discover and apply `AddEmailVerificationAndBlockedSources`. `AlignEmailVerificationSnapshot` records its model without applying its schema twice. Existing accounts are grandfathered as verified by the original migration; new registrations require email activation. Back up an existing database before migration. If tables/columns were created manually outside EF migrations, reconcile their migration history first; do not drop user data to resolve duplicate-column errors.

Configuration uses standard ASP.NET environment variables with `__` separators:

- `ConnectionStrings__PostgresConnection`: PostgreSQL connection string.
- `LocalLlm__Endpoint`, `LocalLlm__ChatEndpoint`, `LocalLlm__Model`: local model API configuration.
- `LocalLlm__SearXngUrl`: SearXNG instance with JSON search enabled.
- `Turnstile__SiteKey`, `Turnstile__Secret`: Cloudflare Turnstile keys valid for the application's hostname.
- `Application__PublicBaseUrl`: public HTTP(S) origin used in activation links.
- `Smtp__Enabled`, `Smtp__Host`, `Smtp__Port`, `Smtp__FromEmail`, `Smtp__Username`, `Smtp__Password`, `Smtp__EnableSsl`: mail delivery configuration.
- `DataProtection__KeysPath`: directory for persistent session protection keys. By default keys are stored under the application's output directory. Production deployments should use durable storage with restricted permissions and share the keys between replicas.

Use environment settings or user secrets for credential values. Protected sessions expire after 12 hours, or 30 days when Remember Me is selected. Password changes, bans and email changes invalidate existing sessions. Old editable `nuctiusnow_user`/`nuctiusnow_userid` fields no longer authenticate users.

Profile updates normalize all supported language names and codes; legacy English/Spanish values remain accepted. A successful update starts the existing three-hour cooldown. Changing email requires verification of the new address; if SMTP is unavailable, the verification page supports resending once delivery is configured.

## Activation email setup

Email delivery is disabled unless `Smtp__Enabled=true` is configured. The settings must be present in the same process that launches ASP.NET (the VSCode Run/Debug profile does not inherit variables entered into a separate PowerShell window). If registration reports that delivery failed, the account still exists: finish preferences and use **Resend** after correcting the mail configuration. Do not register the same account again.

For a local VSCode PowerShell terminal, set the following before starting the application (replace placeholders with your provider's settings; keep credentials out of Git):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Application__PublicBaseUrl = "http://localhost:5217"
$env:Smtp__Enabled = "true"
$env:Smtp__Host = "<provider SMTP host>"
$env:Smtp__Port = "587"
$env:Smtp__EnableSsl = "true"
$env:Smtp__FromEmail = "<provider-approved sender address>"
$env:Smtp__Username = "<SMTP username>"
# Set Smtp__Password securely using your local environment or secret manager.
dotnet run --project NuctiusNow/NuctiusNow --no-launch-profile --urls http://localhost:5217
```

`Application__PublicBaseUrl` is required even when SMTP credentials are correct because it is embedded in the activation link before the SMTP send begins. It must match the URL/port used to open the app. If registration creates the account but reports that delivery failed, read the detailed message on the preferences screen and correct that setting before using **Resend**.

For VSCode F5, put the same non-secret settings in `.vscode/launch.json` under the active profile's `env` section, and put the password in a user secret or hosting environment variable. Restart the debugging session after changing them. A successful send logs the SMTP host and port, never the password.

## Cloudflare Turnstile

Create a Turnstile widget in Cloudflare and authorize each development/production hostname. Set `Turnstile__SiteKey` for the browser widget and `Turnstile__Secret` only on the server. The site and secret keys must come from the same widget. For local development, authorize `localhost` and `127.0.0.1` as applicable. Never commit the secret key.

Use your deployed application's HTTPS origin for `Application__PublicBaseUrl` in production. `System.Net.Mail.SmtpClient` supports STARTTLS (usually port 587); it does not support implicit TLS on port 465. Use your provider's STARTTLS endpoint and, when required, an app password or SMTP credential instead of your normal login password. The server log's `Activation email error` identifies missing configuration, authentication, TLS or network failures. Never share passwords or activation tokens from logs.

## Website languages

User Settings supports English (`en`), Spanish (`es`), Brazilian Portuguese (`pt`), Russian (`ru`), Simplified Chinese (`zh`), Japanese (`ja`), Hindi (`hi`), Arabic (`ar`), French (`fr`), German (`de`) and Italian (`it`). Choose a language and **Apply changes**. The saved profile language takes effect across the site and is restored at sign-in/reload. Profile saves retain the existing three-hour cooldown. Before sign-in, the browser's last selection or supported browser language is used.

Navigation and common controls have a bundled translation catalog. Remaining interface text, page titles, validation messages, news headlines, summaries, snippets and complete generated articles are translated on demand by the configured LM Studio model. The model must be running and capable of translating the selected languages. No new API keys or external translation provider are required. Source news rows stay unchanged; bounded caches isolate translated text by language and source content. Dynamic news and navigation are translated after rendering, so translation can take time on first use. A localized progress/failure indicator and retry button report unavailable translation rather than silently treating English as success.

Arabic sets the document's right-to-left direction. The assistant is instructed to answer in the selected language. Article playback reads the displayed translation and uses that language's speech locale; availability of voices depends on the browser/OS. Form values, URLs, user-written chatbot messages and brand names are preserved.

Run the standalone Chromium localization checks without a database or model:

```bash
cd tests/browser
npm ci
npm run test:localization
```

These checks use a mocked translator to verify all language changes, dynamic content, input preservation, stale response protection, failure/retry and playback text. Automated checks verify model requests and cache behavior, not native-speaker translation quality; review the configured model's translations before production use.

## Personalized feed

Just For You searches each preference from page one and displays completed cards progressively. Existing articles may be reused without overwriting their generated content; duplicates are excluded within the current feed and articles from the previous displayed feed are excluded on refresh. The previous feed is remembered per user in browser session storage, including page reloads. If no alternative is found, the existing cards remain visible with a message. Exploration refreshes also respect these exclusions and blocked sources. A search has a 45-second budget per story, then falls back to cached news for that preference while respecting blocked sources. Optional image lookup has a five-second budget. If the overall refresh times out, cards already found remain visible.

## Regression tests

The integration suite requires PostgreSQL and an account permitted to create temporary databases:

```bash
# Set NUCTIUS_TEST_CONNECTION securely to your PostgreSQL connection.
dotnet test NuctiusNow.sln
```

Each test creates a uniquely named `nuctius_test_*` database, runs real migrations and drops only that database. Tests cover activation, concurrent single-use tokens, mail failure rollback, profile cooldown concurrency, language/birthday persistence, session tampering, blocked-source filtering, and learned preference weights. Email delivery and search HTTP responses use test doubles; no real email is sent.

For the optional Chromium smoke test:

```bash
cd tests/browser
npm ci
# Set CHROMIUM_PATH if Chromium is not /usr/bin/chromium.
# Set NUCTIUS_BROWSER_URL to the running test instance.
# Set NUCTIUS_BROWSER_USERNAME and NUCTIUS_BROWSER_PASSWORD to a fresh verified test account.
# Set NUCTIUS_BROWSER_ARTICLE_IDS to two comma-separated, existing article IDs.
npm test
```

Run against an isolated test database. The browser test changes that account's birthday/language, starts its profile cooldown and logs it out. Articles must have cached AI summary/body and images. Use deterministic test endpoints for SearXNG/LM Studio to test database fallback; the browser blocks external resources and mocks only the CAPTCHA widget API. This does not validate a real Google challenge, SMTP delivery, or real model inference.
