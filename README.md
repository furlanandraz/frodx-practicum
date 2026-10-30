# Order Ingestion Worker

A small .NET 10 worker that fetches orders from an upstream API on startup and then every 15 minutes, checks them against a predefined schema, and stores each order exactly once in SQLite. When something needs a person, it sends an email.

Everything runs locally in Docker: a simulated upstream API, the worker, and a fake mail server that collects the emails.

## Status

| # | Requirement | How |
|---|---|---|
| 1 | Fetch on startup and every 15 min | `BackgroundService` + `PeriodicTimer`; cycles never overlap |
| 2 | Store in a local database | SQLite, plain SQL through Dapper |
| 3 | Never store the same order twice | SHA-256 of order ID + customer ID in `canonical_hash`, unique index, `ON CONFLICT DO NOTHING` |
| 4 | Log every run | One line when each cycle starts and one with its result; console + JSON file, every line tagged with `RunId` |
| 5 | Retry | A failed cycle leaves the cursor where it was, and the next cycle fetches the same window again. Duplicate-proof writes make re-fetching safe. There are no retries within a cycle |
| 6 | Monitoring | The per-cycle log, an email for the failures a person must fix, and Application Insights when configured |
| Bonus | Azure Application Insights | Scaffolded: with a connection string, every log line also goes to Application Insights |
| Bonus | CI/CD with GitHub Actions | Two workflows; the deploy step is mocked (no server) |
| Bonus | Write orders to a file, then import | Not done |

## How a cycle works

1. On startup, and then every 15 minutes, the worker runs one cycle. A cycle never starts while the previous one is still running.
2. It reads the cursor from the database and marks the cycle as `running`.
3. It calls the API once, for orders created since the newest order it already has (minus a 5-minute overlap).
4. **404 or 401:** log, email, return early.
5. **The response's shape doesn't match our schema** (not a list; a field missing, added or of another type): log, email, return early. Nothing is written to the database.
6. **Any other failure** (timeout, network error, 5xx, another 4xx): log, return early. The next cycle tries again from the same cursor.
7. **Otherwise:** orders with an invalid value (a null, an unknown status, …) are skipped and logged. The other orders are stored and the cursor moves, in one transaction. Then the result is logged.

## Architecture

```
 mock-api (simulated upstream)          worker (no endpoints)
 ┌───────────────────────────┐  HTTP   ┌──────────────────────────────────────┐
 │ GET /orders?since=…       │ ◀────── │ IngestionWorker (timer loop)         │
 │  serves simulate/responses│ 1 call  │  └ OrderIngestionService (domain)    │
 │  can fail or slow down    │ a cycle │     ├ IOrderSource → HTTP + schema   │
 └───────────────────────────┘         │     └ IIngestionStore → SQLite       │
                                       │  AlertDispatcher → SMTP              │
                                       └──────┬─────────────────┬─────────────┘
                                              │                 │
                                       logs/, data/ volumes    mailpit (inbox :8025)
```

| Project | Role | References |
|---|---|---|
| `src/OrderIngestion.Domain` | Order model, dedupe hash, cursor, the cycle logic, email rules. No packages, no I/O. | none |
| `src/OrderIngestion.Infrastructure` | Adapters: HTTP client, schema check, SQLite store, SMTP sender. | Domain |
| `src/OrderIngestion.Worker` | Host: config, wiring, timer loop, logging, sending emails. No web server. | Domain, Infrastructure |
| `simulate/OrderIngestion.MockApi` | The simulated upstream API. | none |
| `tests/OrderIngestion.Tests` | Unit, contract and SQLite tests. | Domain, Infrastructure |

References only point inward: Domain knows nothing about HTTP, JSON, SQL or email, so any of them can change without touching it.

## Running it

Requirements: Docker. The .NET 10 SDK is only needed to run outside Docker.

```bash
cp .env.example .env          # required: tells the containers where the other containers are
docker compose up --build
```

Locally and in CI this runs three containers. [`compose.yml`](compose.yml) holds only the worker, the production shape. Docker Compose automatically adds [`compose.override.yml`](compose.override.yml) on top, with the test-only mock API and Mailpit. A server never gets that file: there the worker talks to the real order API and mail server.

| URL | What |
|---|---|
| http://localhost:8080/orders | Simulated upstream API |
| http://localhost:8025 | Mailpit inbox, where the emails arrive |

The worker has no endpoints. It is never exposed, and between cycles there is nothing to serve. Its log shows what it did.

```bash
docker compose logs -f worker                             # readable console log
docker compose exec worker sh -c 'tail -f logs/*.json'    # JSON, one event per line
docker compose restart worker                             # run a cycle now
bash scripts/smoke-test.sh                                # the end-to-end checks CI runs
```

Without Docker (two terminals, from the repo root):

```bash
dotnet run --project simulate/OrderIngestion.MockApi    # http://localhost:8080
dotnet run --project src/OrderIngestion.Worker
dotnet test
```

### Where the database is

The SQLite file is never in the repository.
- **In Docker** it is `/app/data/orders.db` inside the `worker-data` volume, which Docker keeps outside the project folder. To open it in a SQLite viewer, copy it out with `docker compose cp worker:/app/data/. ./data` and open `data/orders.db`. The `-wal` file comes along and holds the latest writes.
- **With `dotnet run`** it is `./data/orders.db` relative to where you start it.
- Both locations are in `.gitignore`. `docker compose down --volumes` deletes the Docker copy.

## Errors

| Failure | What happens | Email |
|---|---|---|
| 404 Not Found: the endpoint is wrong or gone (their side) | Logged, cycle ends | **yes** |
| 401 Unauthorized: our credentials are refused, possibly stale (our side) | Logged, cycle ends | **yes** |
| Schema drift, the shape changed: the response is not JSON or not a list, or an order has a field missing, added or of another JSON type | Logged, cycle ends, nothing stored | **yes** |
| Invalid value in an order: a null, an unknown status, an empty `lines` list, a quantity of 0 or less, a negative amount | Only that order is skipped and logged; the rest of the batch is stored | no |
| Timeout (10 s), network error, 5xx, any other 4xx | Logged, cycle ends; the next cycle tries again | no |
| Unexpected error (e.g. the database) | Logged with stack trace; the next cycle tries again | no |
| Duplicate order | Skipped and counted | no |

- **Every failure ends the cycle early.** The cursor stays where it was, so the next cycle fetches the same window, and nothing between the two is lost.
- **Shape and values are checked separately.** A changed shape means the API changed, which is a reason to stop and look rather than store data we haven't checked: the whole response is rejected and the cursor waits. A bad value is a problem with one order, so only that order is skipped.
- **No nulls, for now.** We don't know yet which fields the API may leave empty, so a null anywhere skips the order. Nulls are checked in the raw JSON before parsing; the parser and `OrderDto` then only ever see complete orders.
- **The worker never stops on a failure.** It logs it, sends the email if one is due, and runs again on the next tick.
- **No email storms.** The same email goes out at most once per `Alerts:ThrottleMinutes` (60), so a 404 that lasts a day doesn't send one every 15 minutes.
- **Mail server problems never stop the worker.** If sending fails, it is logged and the worker keeps running.
- **Invalid configuration stops the worker at startup**, with the name of the setting.

Known limits:
- The email throttle lives in memory, so a restart can resend an email once.
- The dedupe key is identity only (ID + customer), so a status change on an order that is already stored is not picked up.
- Because the shape check is strict, even a harmless new field from the API pauses ingestion until `OrderDto` is updated. That is deliberate.
- A skipped order is not fetched again once the cursor has moved past it. Its ID and reason are in the log.

## Data model

Schema: [`SqliteSchema.cs`](src/OrderIngestion.Infrastructure/Persistence/SqliteSchema.cs). It is applied on every startup with `IF NOT EXISTS`.

| Table | Columns |
|---|---|
| `sync` | `id` INTEGER primary key · `canonical_hash` CHAR(64), unique index (the only index) · `timestamp`, the batch: identical for every row one cycle writes · `payload` JSON, the order exactly as received |
| `cursor` | Exactly one row (`id` = 1, enforced by a CHECK): `last_run` (when the latest cycle started) · `last_run_status` (`running`, `succeeded`, `failed`) · `youngest_record` (createdAt of the newest stored order) |

The database holds only what the sync needs: the hash decides "already stored", the payload carries the data, and the cursor says where to continue. Timing and errors are in the log.

**GDPR.** `sync.payload` holds the full order, including customer data. If the app only needs to sync and must not store personal data, the payload can be left NULL or the column dropped; dedupe works on the hash alone.

SQLite has no real TIMESTAMP or JSON types: it stores both as text. Timestamps are ISO-8601 UTC, which sorts correctly as text, and the JSON column is checked with `json_valid()`.

### The cursor

**Only new orders are fetched.** Each cycle reads `youngest_record` and asks the API for `?since=<youngest_record − 5 min>`. The API filters on its side, so stored orders aren't transferred again. The exception is the small overlap, which catches orders the upstream makes visible a little late; those few re-reads are dropped by the dedupe hash. `Ingestion__CursorOverlapMinutes=0` re-reads only the newest stored order.

**The cursor never reads from before 2026-01-01.** Orders may be older: the first cycle fetches everything, and an old order is stored like any other. But when the newest stored order is older than that date, the next cycle still starts reading at 2026-01-01 (minus the overlap). This also keeps "newest date minus the overlap" away from the smallest date .NET can represent, where the subtraction would fail.

**The read position moves only when orders are saved.** A cycle first marks itself `running`. The new `sync` rows and the new `youngest_record` are then written in one SQLite transaction:

| Where the cycle stops | What is stored | What the next cycle does |
|---|---|---|
| Finished | Orders + new position | Continues after them |
| Any failure: 404, 401, schema drift, timeout, ... | Status `failed`, position unchanged | Fetches the same window again |
| Database error while saving | Transaction rolled back: no orders, position unchanged | Fetches the same window again |
| Process killed, container stopped, or server restarted mid-cycle | Status still `running`, position unchanged | Fetches the same window again and logs "previous run was interrupted" |

So no order can be skipped, and a crash loses only the cycle's work, never data: the orders are still upstream and get fetched again.

**After a restart it doesn't wait.** The worker runs a cycle immediately on startup, reading the position from the database rather than from memory, then continues every 15 minutes.

Both paths are tested. A unit test breaks a save halfway and checks that nothing remains, and the smoke test kills the worker with SIGKILL mid-cycle and checks that the next start stores the missed order.

Limits of a time-based cursor:
- An order that becomes visible upstream more than the overlap after its `createdAt` would be missed.
- If the real API offers a change sequence or an `updated_since` filter, that is the better cursor, stored in place of `youngest_record`.

## Configuration

**Every setting has exactly one default, in the app's `appsettings.json`**: [worker](src/OrderIngestion.Worker/appsettings.json), [mock](simulate/OrderIngestion.MockApi/appsettings.json).
- **Overrides.** Environment variables override any value using `Section__Key`, e.g. `Ingestion__IntervalMinutes=1` for `"Ingestion": { "IntervalMinutes" }`.
- **Docker.** The containers read them from `.env`; `compose.yml` itself contains no values. [`.env.example`](.env.example) holds only what differs inside Docker (where the other containers are) and commented slots for secrets.
- **Startup check.** A missing or invalid value stops the app and names the setting.

| Section | Settings |
|---|---|
| `Ingestion` | `IntervalMinutes`, `CursorOverlapMinutes` |
| `OrderApi` | `BaseUrl`, `OrdersPath`, `TimeoutSeconds` |
| `Database` | `FilePath` |
| `Alerts` | `Enabled`, `SmtpHost`, `SmtpPort`, `SmtpUsername`, `SmtpPassword`, `From`, `To` (comma-separated), `ThrottleMinutes` |
| `Log` | `Level` (`trace`, `debug`, `info`, `warn`, `error`, `fatal`), `Directory` |
| `Mock` (mock API) | `ResponsesPath`, `Scenario`, `FailureRate`, `FailureStatusCode`, `LatencyMs` |
| — | `APPLICATIONINSIGHTS_CONNECTION_STRING` turns on Azure Application Insights |

The email addresses default to `app@example.com` and `user@example.com`. Mailpit accepts any address; set real ones in `.env` before using a real mail server.

## Simulating failures

The mock can be changed while it runs: `PUT /_mock/settings` with the fields to change (the others stay), `DELETE` to reset. Then `docker compose restart worker` to run a cycle immediately, and watch `docker compose logs worker` and the inbox at http://localhost:8025.

```bash
curl -X PUT localhost:8080/_mock/settings -H "Content-Type: application/json" -d '{"scenario":"orders-drift"}'
curl -X DELETE localhost:8080/_mock/settings
```

| To see | Body for `PUT /_mock/settings` |
|---|---|
| A new order arriving | `{"scenario":"orders-new"}` |
| Schema drift: fields added | `{"scenario":"orders-drift"}` |
| Schema drift: broken records | `{"scenario":"orders-broken"}` |
| Invalid values: 2 orders skipped, logged only | `{"scenario":"orders-invalid"}` |
| 404, emailed | `{"failureRate":1,"failureStatusCode":404}` |
| 401, emailed | `{"failureRate":1,"failureStatusCode":401}` |
| 503, logged only | `{"failureRate":1,"failureStatusCode":503}` |
| Timeout, logged only | `{"latencyMs":12000}` |
| Network error, logged only | none: `docker compose stop mock-api` |

| Scenario file | Contents |
|---|---|
| `orders` | 5 valid orders |
| `orders-new` | The same 5 plus one newer valid order |
| `orders-drift` | Orders with fields our schema doesn't have (`channel`, `discountCode`, `shipping`, `lines[].giftWrap`) |
| `orders-broken` | One valid order, plus a missing total, an unknown status, and a number sent as a string (the missing total makes it schema drift) |
| `orders-invalid` | One valid order, one with `customerId: null`, one with `lines: [null]` |

The scenario folder is mounted into the container, so edits apply on the next request without a rebuild.

## Logs

Every cycle writes one line when it starts and one with its result:

```
[17:13:36 info ] Ingestion run 7120cf1a-… started
[17:13:36 info ] Ingestion run 7120cf1a-… completed: fetched 5 since the beginning, inserted 5, duplicates 0, skipped 0 in 104 ms
[17:20:41 warn ] Skipped order #1 ORD-1012: customerId is null
[17:28:36 error] Ingestion run 2b9e41c0-… failed (schema drift, nothing stored): Order #1 (ORD-1008): JSON deserialization for type 'OrderDto' was missing required properties including: 'totalAmount'.
[17:43:36 error] Ingestion run 8d03aa17-… failed (404): Order API returned 404 Not Found for /orders?since=…
[17:58:36 error] Ingestion run 5e7a10b2-… failed (the next cycle tries again): Order API returned 503 Service Unavailable for /orders?since=…
```

Expected failures get one clear line; unexpected ones include the stack trace. The JSON file (`logs/worker-YYYYMMDD.json`, kept 14 days) has the same events with every value as a separate field, including `RunId` and `level`.

## Tests

- **`dotnet test`** (80 tests):
  - Dedupe hash.
  - The schema check: every kind of drift, and every invalid value that skips an order.
  - Contract tests against the mock's own scenario files.
  - HTTP failures sorted into the right kind, with one attempt each.
  - Cursor and cycle logic, including crash recovery and early returns.
  - Email rules and throttling.
  - The SQLite store against a real database file, including rollback of a half-finished save.
- **[`scripts/smoke-test.sh`](scripts/smoke-test.sh)** against the running stack:
  - Startup cycle.
  - A restart stores nothing twice.
  - A worker killed mid-cycle loses nothing.
  - Schema drift (added fields, broken records) stores nothing and emails.
  - Invalid values skip only those orders, are logged, and don't email.
  - A 503 is only logged.
  - A 404 and a 401 email.

## CI/CD

| Workflow | Runs on | Jobs |
|---|---|---|
| [`test.yml`](.github/workflows/test.yml) | Every push, any branch | Build + unit tests → whole stack in Docker + smoke test |
| [`deploy.yml`](.github/workflows/deploy.yml) | A PR merged into `main` | `test.yml` on the merge commit → build the worker image → deploy |

`deploy.yml` runs its own copy of the tests against the merge commit, so a deploy always depends on tests in the same run. A merge is also a push to `main`, so `test.yml` additionally runs on its own.

**The deploy is mocked.** There is no server: the image is built but not pushed, and the deploy job writes the commands it would run into the job summary. Only the worker is deployed; the mock API and Mailpit exist for tests only. To make it real:

1. Set the repository variable `DEPLOY_MODE=ssh`, plus `DEPLOY_HOST`, `DEPLOY_USER` and `DEPLOY_APP_DIR`.
2. Add the secrets `SSH_PRIVATE_KEY` and `SSH_KNOWN_HOSTS` (the output of `ssh-keyscan <host>`).
3. Put `.env` on the server in the app folder, pointing at the real order API and mail server.

The workflow then pushes the image to GHCR, copies `compose.yml` plus [`compose.deploy.yml`](compose.deploy.yml) (which swaps the local build for the CI image), and runs [`deploy/remote-deploy.sh`](deploy/remote-deploy.sh) over SSH. That script fails the job if the new containers don't start.

## Design decisions

- **A fetcher and store, nothing more.** The task is to fetch orders and store them once. There is no downstream system, so there is no dead-letter queue, no outbox, and no status API.
- **The next cycle is the retry.** Every failure ends the cycle with the cursor unchanged, and the next cycle fetches the same window. The writes are duplicate-proof, so that is always safe. There are no retries within a cycle: the job runs every 15 minutes anyway, so a passing blip costs at most one interval, and a single attempt keeps the code and its log easy to follow.
- **No endpoints, no web server.** The worker is never exposed and has nothing to serve between cycles, so it is a plain .NET host on the plain .NET runtime image. What it did is in the log; what needs a person is emailed.
- **A strict schema, checked in two steps.**
  - **Nulls:** the raw JSON of each order is checked for nulls first. An order with one is skipped, so `OrderDto` itself has no nullable fields.
  - **Shape:** `OrderDto` is read with .NET's built-in strict mode (`JsonSerializerOptions.Strict`: every field present, no unknown fields, the right JSON types). Anything else is schema drift: logged, emailed, nothing stored, which catches an API change on day one. The default settings would not do: they silently leave missing fields empty and ignore unknown ones.
  - **Values:** checked in [`OrderMapper.cs`](src/OrderIngestion.Infrastructure/Orders/OrderMapper.cs): a known status, a positive quantity, and so on. A bad value skips only that order.
- **A floor on the cursor, not on orders.** Old orders are valid data; only the read position is kept at or after 2026-01-01 (see [The cursor](#the-cursor)).
- **The mock never leaves the test stack.** It and Mailpit live in `compose.override.yml`, which Docker Compose loads locally and in CI but which is never copied to a server. Only the worker image is built for deployment.
- **The worker knows nothing about tests.** It only ever sends `since`. Tests choose a scenario on the mock itself (`PUT /_mock/settings`), so no test parameter or test mode exists in production code.
- **Email only when a person must act:** 404 (their side), 401 (our credentials), schema drift (our model). A skipped order is only logged. [`AlertRules.cs`](src/OrderIngestion.Domain/Alerts/AlertRules.cs) holds that decision in one tested place.
- **Standard .NET host, three projects.** The task asks for separated components, and separate projects let the compiler enforce the dependency direction. The host provides config with env overrides, dependency injection, logging and clean shutdown, which would otherwise be hand-written.
- **A long-running worker instead of cron.** It runs once on startup and then on a timer, and cycles never overlap.
- **Dapper and plain SQL instead of an ORM.** Every query is visible in [`SqliteIngestionStore.cs`](src/OrderIngestion.Infrastructure/Persistence/SqliteIngestionStore.cs), and there is no migration tooling to learn. Trade-off: moving to SQL Server means porting a handful of statements (`ON CONFLICT` becomes `MERGE`).
- **The dedupe key is a hash of identity.** Order ID + customer ID, length-prefixed so `("AB","C")` and `("A","BC")` differ, then SHA-256. That gives one fixed-length indexed column (`CHAR(64)`), while `id` stays a plain integer primary key. The unique index is the guarantee; the in-memory de-duplication only handles repeats within one response.
- **A cursor with overlap.** Fetches stay incremental, the overlap tolerates late orders, and the hash makes re-reads harmless.
- **Settings have one home.** Defaults live only in `appsettings.json`, `.env` overrides them, and the compose files carry no values. Changing a default touches one file.
- **The domain does no I/O.** `OrderIngestionService` works only through interfaces and returns a result; the host logs it and sends emails. That is what makes it testable with simple fakes.
- **Logging is configured in code.** Serilog with short level names (`info`, `warn`, `error`), configured in code so every choice is visible in [`LoggingSetup.cs`](src/OrderIngestion.Worker/Logging/LoggingSetup.cs).
- **An own mock API.** jsonplaceholder has no orders and never fails. The mock can return errors, slow down, filter by `since`, and serve drifted or broken payloads.
- **Containers.** Alpine-based .NET images and a non-root user. The worker uses the plain runtime; the mock API, a web server, uses the ASP.NET image and has a health check.
- **`InvariantGlobalization`.** Numbers and dates format the same on a Slovenian dev machine, in CI, and in containers.
- **Application Insights through OpenTelemetry, off by default.** With a connection string, the log, including every cycle's start and result, is exported to Application Insights. Without one, nothing changes.

## Roadmap

1. Metrics for Application Insights (fetched, inserted, failed cycles, cycle duration) through an OpenTelemetry meter.
2. Bonus: archive each raw response to a file and import from it.
3. If status updates matter: a content hash to detect changes to orders that are already stored.
