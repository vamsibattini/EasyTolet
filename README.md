# EasyTolet Microservices Solution

This repository contains the scaffold for the EasyTolet microservices architecture based on the provided Excel plan.

## Contents

- `src/EasyTolet.Shared` - shared building blocks for repository pattern, unit of work, RabbitMQ settings, and middleware.
- `src/EasyTolet.Gateway` - API gateway scaffold using YARP / JWT validation.
- Each microservice project under `src/` implements a clean architecture scaffold with EF Core, repository/UoW, custom error middleware, and RabbitMQ configuration.

## Next steps

1. Install the .NET SDK (`dotnet`).
2. Open this folder in VS Code.
3. Restore packages with `dotnet restore`.
4. Build each project or add them to a solution file.
5. Configure actual database connection strings and RabbitMQ hosts.


## Database access

See [database setup and usage](docs/database-access.md) for per-service MySQL configuration, secret setup, EF Core/Dapper SQL, stored procedures, functions, transactions and tests.

## Automated tests

Every service, the gateway, and Shared have an xUnit test project. See [test projects and commands](tests/README.md). New behavior and bug fixes must include corresponding tests, as recorded in [AGENTS.md](AGENTS.md).

## Exception logging

All services use the shared exception handler with daily text files and MySQL inserts. Execute [ExceptionLogs.sql](database/ExceptionLogs.sql) in each owning schema, then follow [logging setup and behavior](docs/exception-logging.md).

## AdminService Roles

Roles CRUD, validation, JWT permission checks and RabbitMQ events through a transactional outbox are implemented. See [AdminService setup, endpoints and tests](docs/admin-roles.md); execute [AdminOutboxMessages.sql](database/AdminOutboxMessages.sql) in the Admin database before using role mutations.
