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
- `Recaptcha__SiteKey`, `Recaptcha__Secret`: keys valid for the application's hostname.
- `Application__PublicBaseUrl`: public HTTP(S) origin used in activation links.
- `Smtp__Enabled`, `Smtp__Host`, `Smtp__Port`, `Smtp__FromEmail`, `Smtp__Username`, `Smtp__Password`, `Smtp__EnableSsl`: mail delivery configuration.
- `DataProtection__KeysPath`: directory for persistent session protection keys. By default keys are stored under the application's output directory. Production deployments should use durable storage with restricted permissions and share the keys between replicas.

Use environment settings or user secrets for credential values. Protected sessions expire after 12 hours, or 30 days when Remember Me is selected. Password changes, bans and email changes invalidate existing sessions. Old editable `nuctiusnow_user`/`nuctiusnow_userid` fields no longer authenticate users.

Profile updates normalize supported English/Spanish language values to `en`/`es`. A successful update starts the existing three-hour cooldown. Changing email requires verification of the new address; if SMTP is unavailable, the verification page supports resending once delivery is configured.

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
