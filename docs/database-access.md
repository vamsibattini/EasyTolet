# EasyTolet database access

Every microservice registers **one database** and one scoped EF Core `DbContext`. Shared code is reusable; databases, credentials, EF entities and transactions are service-owned. Database grants enforce isolation: give each runtime account access only to its own schema. Do not choose a connection, schema, table or routine from a request parameter.

## Connection check and local setup

The supplied Workbench screenshot specifies TCP `localhost:3306`, user `root`, and no default schema. The previous UserService setting matched the host/user but had an empty password. A live read-only check reached MySQL and failed with error 1045 (`using password: NO`). Other services previously used SQL Server's `Trusted_Connection=True`; those settings have been replaced.

Passwords are absent from source configuration. Each service has a `UserSecretsId`. Store your local password with a hidden prompt (not in this chat or a shell command argument):

```sh
python3 scripts/set-database-password.py EasyTolet.UserService
```

The script sends JSON over stdin to `dotnet user-secrets set`. User-secrets are development-only files outside the repository, **not encrypted storage**. Do not use them in production. Workbench's saved password is not automatically available to .NET.

Use Workbench to run `SELECT VERSION();` and inspect the existing schemas. Set `Database:ServerVersion` to the actual numeric version, for example `8.0.42` or `8.4.4`. The checked-in `8.0.0` is a compatibility default, not a verified server version. Registration deliberately does not connect to the database for version detection. This helper targets MySQL 8+, not MariaDB.

```sh
dotnet user-secrets set "Database:ServerVersion" "8.0.42" --project src/EasyTolet.UserService
dotnet user-secrets set "Database:Database" "your_existing_schema" --project src/EasyTolet.UserService
dotnet run --project src/EasyTolet.UserService -- --environment Development
```

Replace the example version/schema with actual values. UserService retains the original schema name `EasyToLet.UserServiceDb`; other services retain their own configured names. MySQL schema names can be case sensitive on Linux. No schemas, accounts, application tables, functions or stored procedures were created by this change. Startup success and existing HTTP health endpoints do not verify database connectivity.

`appsettings.Development.json` allows `root` and `SslMode=Preferred` for local development only. Outside Development, validation rejects root and requires `VerifyFull`. Provision a dedicated user such as `easytolet_user`, a trusted server certificate, and optionally configure `Database:SslCa`. `VerifyFull` validates certificate trust and hostname; `Preferred` may connect without encryption.

Supply production passwords with a deployment secret provider or `Database__Password`. Other environment keys include `Database__Server`, `Database__Port`, `Database__Database`, `Database__User`, `Database__ServerVersion`, and `Database__SslCa`. Pool limits are per service instance; budget total connections across replicas. Runtime users normally need schema-scoped SELECT/INSERT/UPDATE/DELETE and appropriate routine EXECUTE grants. Use a separate migration/provisioning account for DDL; never grant runtime accounts cross-service access. Stored routines should use `SQL SECURITY INVOKER` unless an explicitly reviewed definer is necessary.

A secret-supplied `ConnectionStrings__DefaultConnection` remains an optional **complete override**. Its user/TLS/settings are still validated, and unsafe connector flags are forcibly disabled. It takes precedence over structured Database settings; avoid setting both. Never log a connection string.

Compose now expects one password variable per service, such as `USERSERVICE_DB_PASSWORD` and `PROPERTYSERVICE_DB_PASSWORD`; supply these through your deployment environment. It uses `host.docker.internal` to reach a host MySQL server and defaults to production TLS/dedicated users. Existing Dockerfile/build-context scaffolding is not validated by this work. The gateway has no business database; its separate exception-logging database is described in [exception logging](exception-logging.md).

## Files and packages

```text
src/EasyTolet.Shared/src/Shared/
  Database/
    MySqlDatabaseOptions.cs
    MySqlConnectionStringHelper.cs
    DatabaseServiceCollectionExtensions.cs
    SqlExecutor.cs
  Interfaces/
    IRepository.cs
    IUnitOfWork.cs
    ISqlExecutor.cs
  Infrastructure/
    Repository.cs
    UnitOfWork.cs
```

Shared uses EF Core/Relational 9.0.0, Pomelo.EntityFrameworkCore.MySql 9.0.0, Dapper 2.1.66, and the ASP.NET Core framework reference. Pomelo supplies MySqlConnector transitively. Existing services retain their project reference to Shared. Service registration is:

```csharp
builder.Services.AddMySqlDatabase<AppDbContext>(builder.Configuration, builder.Environment);
```

This registers `AppDbContext`, its `DbContext` alias, `IRepository<>`, `IUnitOfWork`, and `ISqlExecutor` with scoped lifetimes. A second database registration in the same host is rejected. No new API endpoints or EF migrations are needed for this infrastructure change. Add each service's business entities and migrations separately; never create a shared business DbContext or run automatic migrations with runtime credentials.

## Choosing an operation

Inject `IUnitOfWork` for tracked EF entity CRUD. `InsertAsync`, `UpdateAsync`, and `DeleteAsync` stage changes; call `SaveChangesAsync` to persist them. Old repository `*AndSaveAsync` methods are deprecated for compatibility: they still immediately flush **all** pending context changes. Do not use them for new code.

Inject `ISqlExecutor` for SQL. EF query results are unmapped DTOs/scalars, not tracked entities. Dapper supports DTOs/scalars and `DynamicParameters` (including output parameters). Example calls below assume your own User entity/DTO and SQL routines exist:

```csharp
// EF handles interpolated values as database parameters.
var users = await sql.QueryEfAsync<UserDto>(
    $"SELECT Id, Name FROM Users WHERE Id = {userId}", ct);
await sql.ExecuteEfAsync($"UPDATE Users SET Name = {name} WHERE Id = {userId}", ct);

// Dapper: SQL text is fixed; all external values go in the parameters object.
var rows = await sql.QueryAsync<UserDto>(
    "SELECT Id, Name FROM Users WHERE Id = @Id", new { Id = userId }, ct);
var count = await sql.ScalarAsync<long>("SELECT COUNT(*) FROM Users", cancellationToken: ct);
await sql.ExecuteAsync("UPDATE Users SET Name = @Name WHERE Id = @Id",
    new { Id = userId, Name = name }, ct);

// Procedures: pass a routine name, not CALL syntax.
var result = await sql.QueryProcedureAsync<UserDto>("SP_GetUser", new { p_id = userId }, ct);
await sql.ExecuteProcedureAsync("SP_UpdateUser", new { p_id = userId, p_name = name }, ct);
var formattedId = await sql.FunctionAsync<string>("FN_FormatUserId", new object?[] { 42L }, ct);

var parameters = new Dapper.DynamicParameters();
parameters.Add("p_id", userId);
parameters.Add("p_total", dbType: System.Data.DbType.Int32,
    direction: System.Data.ParameterDirection.Output);
await sql.ExecuteProcedureAsync("SP_GetUserTotal", parameters, ct);
var total = parameters.Get<int>("p_total");
```

Never interpolate or concatenate external values into a Dapper SQL string. EF methods accept `FormattableString` directly: do not convert interpolation to `string` first. Identifiers cannot be SQL parameters: use application-owned constants or explicit allowlists. Function/procedure names are validated unqualified identifiers to prevent accidental cross-database routing. This is not a SQL firewall; service account grants remain essential.

`QueryProcedureAsync` buffers the first result set. Multiple result sets, streaming, bulk imports and provider-specific parameter types require a dedicated service repository; the shared API does not pretend every database feature has the same semantics. Use `QueryAsync` for parameterized SQL joins or projections; avoid loading entire large tables through repository `ListAsync`.

The legacy UoW `ExecuteStoredProcedureAsync` is deprecated. It retains text/CALL semantics but now requires named input `DbParameter` values. Migrate to `ISqlExecutor` for procedure names, output parameters and correct Dapper mapping.

## Transactions and lifetime

```csharp
await unitOfWork.ExecuteInTransactionAsync(async token =>
{
    await unitOfWork.InsertAsync(user, token);
    await unitOfWork.SaveChangesAsync(token); // Flush to obtain generated ID or let SQL see it.
    await sql.ExecuteAsync(
        "INSERT INTO UserAudit (UserId, Action) VALUES (@UserId, @Action)",
        new { UserId = user.Id, Action = "Created" }, token);
}, ct);
```

The callback's remaining tracked changes are saved and the transaction committed at the end. An exception/cancellation rolls back and clears the change tracker; discard affected entity instances/reload them afterward (generated IDs and in-memory values are not reverted). Nested transactions are rejected. EF and Dapper use the same connection and current EF transaction. Dapper opens/closes a previously closed connection and leaves an already open connection alone; the DbContext owns disposal.

SQL writes execute immediately; they do **not** wait for `SaveChangesAsync`. Outside an explicit transaction they can auto-commit. Raw SQL/Dapper also bypass EF change tracking, concurrency checks, query filters and domain validation. Reload tracked entities if SQL changes their rows. Do not execute parallel operations on a scoped DbContext. Do not call external APIs, send messages, or switch databases within this transaction. Add an outbox separately when messaging is implemented. Automatic retries are deliberately not enabled for mixed operations with potentially non-idempotent effects.

Use transactional tables (InnoDB). MySQL DDL and routines containing COMMIT/ROLLBACK can break the caller's transaction boundary; only use transaction-neutral routines in this callback. Cancellation during commit can leave an uncertain outcome—business commands still need idempotency where appropriate.

## Verification

```sh
dotnet build EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
dotnet test tests/EasyTolet.Shared.Tests --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Automated SQLite relational tests cover shared transaction commit/rollback, cancellation, parameterized hostile strings, routine name validation, and connection security validation. They do not establish MySQL compatibility by themselves. A separate MySQL integration test is skipped unless `EASYTOLET_TEST_MYSQL` contains a connection string to a dedicated existing database whose name ends in `_tests`. That test creates uniquely named test objects, exercises procedure output parameters/functions/mixed rollback, and removes its own objects. It requires CREATE/ALTER ROUTINE and table DDL permissions; never point it at application data. No MySQL integration credentials were available during implementation.

The solution's existing MassTransit 9.0.10 references currently resolve to 9.1.0 and emit NU1603 warnings; they are unrelated to database access.

Reference behavior: [EF Core transaction sharing](https://learn.microsoft.com/en-us/ef/core/saving/transactions), [MySqlConnector connection security options](https://mysqlconnector.net/connection-options/), [Dapper CommandDefinition](https://github.com/DapperLib/Dapper/blob/main/Dapper/CommandDefinition.cs).
