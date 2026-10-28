# EasyTolet development requirements

## Tests accompany implementation

The user requires test cases for future implementation work. Whenever adding or changing application behavior, include meaningful automated tests in the same change. Fixes should include a regression test. Cover success, invalid input, failure paths, and authorization or transaction boundaries where relevant.

- Each existing service and the gateway has `tests/EasyTolet.<Service>.Tests`; shared infrastructure tests live in `tests/EasyTolet.Shared.Tests`.
- When creating another service, also create its xUnit test project, reference that service, and add the test project under the solution's `tests` folder.
- Use the existing .NET 9/xUnit conventions. Organize tests by feature or layer as code grows; use descriptive behavior-oriented names and Arrange/Act/Assert.
- Unit tests must run without MySQL, RabbitMQ, external services, passwords, or application startup. Use fakes/mocks at external boundaries when necessary.
- Use integration tests for database/provider behavior and HTTP pipeline behavior; unit tests alone cannot verify SQL routines, routing, authentication middleware, or external systems.
- Integration tests requiring infrastructure must be explicitly configured and isolated from application/production data. Report skipped tests honestly.
- Avoid placeholder passing tests and tests that only check that an object exists. Documentation-only changes need no artificial tests.
- Run affected tests and build the solution before declaring implementation complete. Report commands, failures, skipped tests and any verification limitations.

## Commands

```sh
dotnet build EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
dotnet test EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

See `tests/README.md` for the current coverage and focused test commands.

## Required controller dependencies

The user requires every production controller to receive its service's concrete `AppDbContext` and `IExceptionReporter` through constructor injection. Inherit `ServiceControllerBase<AppDbContext>` and pass both dependencies to its constructor; its protected `Database` and `ExceptionReporter` properties expose them. Additional application service dependencies may also be injected.

Do not inject or manually invoke `ErrorHandlingMiddleware` from controllers. It is registered once in the HTTP pipeline and automatically reports unhandled exceptions. Use the injected reporter only for deliberately handled exceptions; do not report and rethrow the same exception, which would create duplicate logs. Do not create/dispose contexts manually or access another service's context.

The gateway uses its own `AppDbContext` configured from `ExceptionLogging:Database` for operational logging only. It must not query downstream service databases. All controllers, including health controllers, follow this constructor rule. Each service test project has a constructor contract test that must continue to cover all concrete production controllers as new ones are added.

## Admin business repositories

AdminService business repositories must constructor-inject the service AppDbContext and IExceptionReporter, and reuse the shared generic repositories/IUnitOfWork for entity mutations. Do not double-log exceptions. Role mutations must commit their integration event in the same transaction via AdminOutboxMessages; do not publish to RabbitMQ directly from a controller before the database transaction commits. Keep cross-service contracts in EasyTolet.Contracts and test business rules, authorization and transactional failure paths.
