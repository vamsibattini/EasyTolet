# Shared exception handling and logging

All 14 services and the gateway register `AddGlobalExceptionHandling(builder.Configuration)` and call `UseGlobalExceptionHandling()` before authentication, authorization and endpoint execution. There is one shared implementation in `EasyTolet.Shared/Exceptions`; each process has its own registered instance. No controller/repository-level catch-and-log boilerplate is needed. Use `throw;` when rethrowing so the original stack is preserved.

## MySQL Workbench setup

Open [database/ExceptionLogs.sql](../database/ExceptionLogs.sql), select the owning service's schema as the default schema, and execute the script. Repeat for each service schema. The application performs parameterized INSERTs; it does not create the table or database automatically. No EF migration is needed for this manually managed operational table.

Each service logs to its own configured `Database` connection by default. It opens a **separate MySqlConnector connection**, not the failed request's DbContext or Unit of Work. Automatic ambient transaction enlistment is disabled, so transaction rollback cannot remove the log record. The runtime/logging account needs INSERT permission on ExceptionLogs. Use an administrative account for table creation, investigations and retention. Retain database-per-service access boundaries.

The gateway has no business database. Its controller AppDbContext uses its operational logging database, and valid connection settings are required at startup. Its `ExceptionLogging:Database` section targets `EasyTolet.GatewayLogsDb`; create this logging schema/account and run the same table script there. Set its local password with the hidden prompt:

```sh
python3 scripts/set-database-password.py EasyTolet.Gateway --logging
```

Other services reuse the password configured with `scripts/set-database-password.py EasyTolet.UserService` (replace the service name as appropriate). User-secrets are loaded in Development, are stored outside source control, and are not encrypted. The existing MySQL authentication failure must be resolved before database logs can be inserted.

Optionally configure a complete `ExceptionLogging:Database` section to use an INSERT-only account per service. Alternatively, `ConnectionStrings:ExceptionLogging` supplies a complete secret connection string override. The same connection validation applies: dedicated non-root accounts and VerifyFull TLS outside Development. In deployments use a secret provider or `ExceptionLogging__Database__Password`; never put passwords into tracked JSON. Compose's gateway logging password is supplied via `GATEWAY_LOG_DB_PASSWORD`.

## Configuration and daily files

Every host has this configuration:

```json
"ExceptionLogging": {
  "Directory": "logs/exceptions",
  "DatabaseEnabled": true,
  "SinkTimeoutSeconds": 5
}
```

Files are written under the service content root (or an absolute configured Directory):

```text
logs/exceptions/<service>/<process-id-and-instance-guid>/exceptions-2026-09-19.txt
```

The day is **UTC**. A file is created on the first exception that day; idle days do not create empty files. Each file contains one JSON record per line, with escaped newlines in stack traces. This keeps concurrent entries parseable and prevents line-break injection from forging another record. Files and directories are created with owner-only permissions on Unix; configure equivalent filesystem ACLs on Windows and secure any pre-existing directories. Files are outside wwwroot and ignored by Git.

A singleton lock serializes appends within a process. Each process instance has its own directory so multiple replicas do not append to the same file. Mount a persistent writable log volume for containers. Set disk/database retention and monitoring for your deployment; this implementation does not delete old records/files automatically.

## Captured details

Both destinations receive the same record ID and payload:

| Field | Meaning |
| --- | --- |
| Id / OccurredAtUtc | Unique exception record and UTC timestamp |
| Service / Environment / Instance | Failing host and process instance |
| CorrelationId / TraceId | Request correlation and active distributed trace, when available |
| Method / Path | HTTP method and path, without query string |
| Controller / Action | MVC controller type and action method from endpoint metadata |
| Repository | Repository type names identified in exception stack frames; nullable |
| StatusCode / ResponseStarted | Intended error status and whether headers were already sent |
| ExceptionType / Message | Error type and message |
| ExceptionDetails | Exception.ToString(): full available stack and inner exception details |

Repository identification uses types named `*Repository*` in available stack frames; it is diagnostic metadata, not proof that a repository was called. Stack traces also show application/service methods. Controller/action can be null for errors before routing or in non-MVC endpoints. Missing frames, optimized/inlined methods and missing PDBs cannot be reconstructed. Source filenames/line numbers appear only when symbols provide them; variable values are not captured.

Request bodies, headers, cookies, query strings and exception.Data are not collected. Common password/token/secret assignments and Bearer tokens in exception text are redacted. This is defense in depth, not a universal secret detector: never embed credentials, personal data or connection strings in exceptions, SQL messages or URL paths. Limit log access as operational records may still contain sensitive application details.

## Error behavior and limitations

- Unknown exceptions return HTTP 500 with a generic problem JSON response. Valid ApiException statuses (400–599) are preserved. Raw exception messages/stacks are never returned, including in Development.
- The response and `X-Correlation-ID` header carry the correlation ID for investigation.
- Both sinks are attempted independently, with separate cancellation deadlines. Logging does not use the aborted request token, so an application error can still be recorded after a disconnect.
- A file failure does not prevent MySQL insertion, and a database failure does not prevent file logging. Sink failures emit a sanitized console/provider error containing the record ID, sink name and failure type.
- If both destinations fail, durable storage cannot be guaranteed. There is no background replay or retry queue. The request waits for logging; during an outage expect roughly the configured timeout plus connector cancellation/cleanup overhead. Extensions must honor cancellation.
- If the HTTP response has already started, the error is logged and rethrown; an error response cannot safely replace committed headers/body. Request-aborted OperationCanceledException is propagated without treating it as an application failure.
- Middleware catches unhandled exceptions in the downstream HTTP pipeline. It cannot catch exceptions already swallowed by code, startup failures, process crashes, or background worker failures. Background services can use the same IExceptionReporter with their own record/context.

## Tests

Shared tests cover safe HTTP responses, controller/repository metadata, inner stacks, redaction, valid/invalid error statuses, started responses, request cancellation, sink failure isolation, sink timeouts, concurrent append, daily rollover and a real TestServer HTTP pipeline. Existing service health tests continue to run.

An opt-in MySQL integration test uses `EASYTOLET_TEST_MYSQL` (a dedicated existing schema ending in `_tests`), creates ExceptionLogs from the supplied script, verifies parameterized insertion and independence from ambient rollback, and deletes only its own record. The table remains in the test schema. This test is skipped when credentials are unavailable.

```sh
dotnet test EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Reference behavior: [ASP.NET Core error handling](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-9.0) and [MySqlConnector AutoEnlist](https://mysqlconnector.net/api/mysqlconnector/mysqlconnectionstringbuilder/autoenlist/).

## Required controller injection

Every production controller receives its service-specific context and the shared exception reporter:

```csharp
public class UsersController : ServiceControllerBase<AppDbContext>
{
    public UsersController(AppDbContext database, IExceptionReporter exceptionReporter)
        : base(database, exceptionReporter) { }
}
```

Import `EasyTolet.Shared.Controllers` and `EasyTolet.Shared.Exceptions`. `Database` and `ExceptionReporter` are protected properties. Add application service dependencies as features grow. ASP.NET Core owns the scoped context lifetime; controllers must not dispose it.

The reporter is the injectable exception logging service; the middleware remains registered once in the HTTP pipeline. Allow unhandled errors to propagate to it. Only explicitly handled failures need manual reporting—never report and rethrow the same exception. Each service test project checks this constructor contract for every concrete controller. Health actions receive these dependencies but do not query the database.
