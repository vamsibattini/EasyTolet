# AdminService: Roles

This first AdminService feature maps the existing Roles table you supplied. AdminService owns role definitions and their hierarchy. It does not yet implement permission assignments, role-to-user mappings or other admin tables; supply those schemas before adding their models/endpoints. Identity remains responsible for authentication and issuing permissions. AdminService publishes role changes; other services retain ownership of their databases.

## Structure

```text
src/EasyTolet.AdminService/
  Controllers/AdminController.cs
  Contracts/RoleRequests.cs
  Domain/Role.cs
  Domain/AdminOutboxMessage.cs
  Repositories/IRoleBusinessRepository.cs
  Repositories/RoleBusinessRepository.cs
  Infrastructure/AppDbContext.cs
  Infrastructure/AdminServiceCollectionExtensions.cs
  Messaging/AdminOutboxDispatcher.cs
  Messaging/AdminOutboxWorker.cs
src/EasyTolet.Contracts/
  Admin/RoleChangedV1.cs
```

Business logic stays in the AdminService repository folder for now. The separate Contracts library contains only integration DTOs, no EF entities or DbContext. Existing generic repositories and IUnitOfWork perform mutations; EF projections/pagination and hierarchy queries live in the business repository. Both controller and repository receive AppDbContext and IExceptionReporter. They let unhandled exceptions reach the shared middleware rather than logging them twice. Expected database conflicts preserve the original exception for diagnostic logging.

## Database setup

1. Configure AdminService's MySQL connection to the schema where your Roles table exists. Case matters on some MySQL hosts; the current default is `EasyTolet.AdminServiceDb`.
2. Keep the existing Roles table and its data. The model maps its identity key, unique RoleCode, self-referencing ParentRoleId, field lengths, defaults and audit fields. It does not reset AUTO_INCREMENT or seed roles.
3. Execute [AdminOutboxMessages.sql](../database/AdminOutboxMessages.sql) in that same schema. This is required for reliable events; if saving an outbox record fails, the entire role change rolls back.
4. Ensure [ExceptionLogs.sql](../database/ExceptionLogs.sql) has been executed for the logging sink.

No automatic migrations, schema recreation or database provisioning run at startup. These scripts are the deployment schema changes for this step; no `dotnet ef database update` is needed. Before adopting migrations later, baseline the pre-existing schema rather than applying an initial migration that recreates Roles.

```sh
python3 scripts/set-database-password.py EasyTolet.AdminService
dotnet user-secrets set "Database:Database" "your_admin_schema" --project src/EasyTolet.AdminService
dotnet user-secrets set "Database:ServerVersion" "your_numeric_mysql_version" --project src/EasyTolet.AdminService
```

Use actual values in place of placeholders. Production requires a dedicated MySQL user and VerifyFull TLS as described in [database access](database-access.md). Runtime permissions include SELECT/INSERT/UPDATE/DELETE on Roles, SELECT/INSERT/UPDATE on AdminOutboxMessages, and INSERT on ExceptionLogs. DDL and retention belong to a separate administrative account.

## Endpoints

All routes require a valid JWT with `permission=admin.roles.manage`. Mutations additionally require a positive integer `user_id` claim to match CreatedBy/UpdatedBy's INT schema. The public string identifier such as `UETID_00000007` may remain `sub`; Identity must also issue the internal numeric `user_id`. Audit IDs are never accepted from the request body.

| Method | Endpoint | Result |
| --- | --- | --- |
| GET | `/api/admin/roles?page=1&pageSize=20` | Paged role DTOs and total count |
| GET | `/api/admin/roles/{id}` | Role DTO or 404 |
| POST | `/api/admin/roles` | 201 with DTO and Location header |
| PUT | `/api/admin/roles/{id}` | Updated DTO |
| DELETE | `/api/admin/roles/{id}` | 204; hard-delete a leaf role |

POST/PUT body:

```json
{
  "roleCode": "PO",
  "roleName": "Property Owner",
  "parentRoleId": null,
  "description": "Manages owned properties",
  "isActive": true
}
```

RoleCode accepts 1–10 ASCII letters, digits, underscores or hyphens and is normalized to uppercase. RoleName is required, trimmed, and at most 100 characters. Description is at most 500 characters. The database unique index remains authoritative, including its case/accent-insensitive collation. CreatedDate/CreatedBy are server controlled and preserved during updates; UpdatedDate/UpdatedBy are set by the server. Timestamp values are interpreted as UTC (including existing MySQL datetime values—convert old data first if it was stored in a local time zone).

Missing roles return 404. Invalid fields, missing parents or invalid pagination return 400. Duplicate codes, self-parenting, ancestry cycles, deletion of parents, foreign-key conflicts and concurrent transaction conflicts return 409. The global middleware returns generic safe error details; diagnostic logs retain the specific cause. Page size is 1–100. PUT replaces all editable fields, including ParentRoleId; it is not a partial PATCH. Requests do not provide an optimistic version: concurrent valid edits follow the database transaction order.

Hierarchy checks run inside a Serializable MySQL transaction, with the role update and outbox insert committed together. For small role-master tables this favors correctness; competing writes may deadlock and return 409 so the caller can reload/retry. No automatic request retry is performed. Future foreign keys may block deletion; otherwise DELETE is a real delete. Use PUT with `isActive=false` when deactivation is intended. Current cross-service role assignments and existing JWTs are not automatically revoked; Identity/consumer work is needed once those schemas are provided.

## JWT configuration and local running

AdminService now configures JWT bearer validation from `Jwt:Authority` and `Jwt:Audience`, requiring issuer, audience, lifetime and signing-key validation. The checked-in `https://identity.local` authority is a placeholder, not a functioning identity provider. Configure a real HTTPS OIDC authority and expected audience before invoking protected endpoints; there is no development authentication bypass. Token tests use a separate signed test-only issuer/key.

```sh
dotnet user-secrets set "Jwt:Authority" "https://your-identity-provider" --project src/EasyTolet.AdminService
dotnet user-secrets set "Jwt:Audience" "EasyTolet.AdminServiceApi" --project src/EasyTolet.AdminService
dotnet run --project src/EasyTolet.AdminService -- --environment Development --urls http://localhost:5015
```

Swagger is available at `/swagger`; send the JWT in the Authorization header when calling these APIs. Gateway forwarding for `/api/admin/*` is not yet configured in the gateway scaffold.

## RabbitMQ and the outbox

AdminService uses MassTransit/MassTransit.RabbitMQ **8.5.8** (Apache-2.0) rather than the scaffold's unresolved 9.0.10 dependency. Other service scaffolds retain their existing packages until their messaging is implemented. The Contracts project is explicitly referenced by AdminService and should be referenced by future consumers.

`RabbitMq:Address` is an absolute `rabbitmq://host/virtual-host` or `rabbitmqs://host/virtual-host` URI. Development defaults to `rabbitmq://localhost/`; normal configuration uses `rabbitmq://rabbitmq/`. The old `RabbitMq:Host` setting is not used by this AdminService registration. Configure username/password via secret configuration or `RabbitMq__Username`/`RabbitMq__Password`; guest settings are local scaffolding only. Use a dedicated broker user/vhost and trusted TLS for deployment. Existing Compose supplies guest credentials for local Docker development.

The background dispatcher polls every five seconds, processes up to 50 pending messages, and sets PublishedAtUtc only after publish succeeds. Batch cancellation is 30 seconds. An unavailable broker leaves records pending for retry. Background failures are explicitly sent to the same file/database exception reporter. A successful mutation means the role and event are saved locally; it does not mean every downstream service has already processed the change.

`RoleChangedV1` carries EventId, monotonically allocated outbox Sequence, OccurredAtUtc, Operation (`Created`, `Updated`, `Deleted`), ActorId, and a full RoleSnapshot. Deleted events carry the removed role's snapshot. This is **at-least-once** delivery: a crash after publishing but before marking a row can duplicate it. Multiple Admin replicas may also publish the same row. Consumers must persist deduplication by EventId and compare Sequence per RoleId atomically with their local projection update; retain sequence tombstones after deletes to reject stale events. Auto-increment sequence gaps are valid. Restoring/reseeding outbox IDs requires consumer reconciliation.

Consumers must reference the same contract and configure a durable queue before events are published. The default MassTransit exchange name comes from `EasyTolet.Contracts.Admin.RoleChangedV1`; do not rename the contract to match a local entity. Example future consumer registration:

```csharp
services.AddMassTransit(bus =>
{
    bus.AddConsumer<RoleChangedConsumer>(); // Implement local projection + idempotent inbox for that service.
    bus.UsingRabbitMq((context, rabbit) =>
    {
        rabbit.Host(new Uri(brokerAddress), host =>
        {
            host.Username(brokerUser);
            host.Password(brokerPassword);
        });
        rabbit.ReceiveEndpoint("identity-admin-role-changes-v1", endpoint =>
            endpoint.ConfigureConsumer<RoleChangedConsumer>(context));
    });
});
```

Each subscribing service needs its own queue, not a single shared competing-consumer queue across all services. No downstream consumer was invented without its table/model contract. RabbitMQ can accept a publication with no bound queues; broker confirmation is not proof of downstream processing. Provision consumers before enabling writes, or plan an initial Roles API snapshot/backfill. Existing Roles rows do not automatically generate events.

Pending records are never deleted automatically. Poison/corrupt payloads stop that batch and are retried; monitor the oldest pending row and exception logs, repair/quarantine bad records operationally, and archive published rows using a deliberate retention policy. MassTransit retry, queue provisioning, backups and operational monitoring remain deployment responsibilities.

## Tests and verification

```sh
dotnet build EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
dotnet test EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Admin tests cover mapped CRUD, audit fields, inactive roles, validation, paging, parent cycles, parent deletion, unique role codes, cancellation, rollback when the outbox fails for create/update/delete, failed publishing/retry, and stable event identity. HTTP TestServer tests use signed JWTs for 401/403 checks, audit-claim requirements, DTO validation, routes and the Location header. SQLite relational tests do not prove MySQL collation/locking behavior.

Optional external integration tests:

- Set `EASYTOLET_ADMIN_TEST_MYSQL` to a dedicated existing MySQL database ending in `_tests`. The test creates test tables if absent, uses uniquely coded role rows, and removes its own rows/events. Do not use production/application data.
- Set `EASYTOLET_TEST_RABBITMQ_ADDRESS`, `EASYTOLET_TEST_RABBITMQ_USER`, and `EASYTOLET_TEST_RABBITMQ_PASSWORD` to a dedicated broker test vhost. The test creates an ephemeral queue and verifies real publish/consume; broker topology may remain in the isolated vhost afterward.

Without those variables the tests explicitly skip; no live MySQL/RabbitMQ verification is claimed. Existing unrelated service MassTransit package warnings remain.

References: [MassTransit 8.5.8 package](https://www.nuget.org/packages/MassTransit/8.5.8), [MySQL Serializable read locks](https://dev.mysql.com/doc/refman/8.0/en/innodb-locks-set.html), [JWT bearer configuration](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-jwt-bearer-authentication?view=aspnetcore-9.0).
