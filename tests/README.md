# EasyTolet tests

Every existing production project has a corresponding xUnit project in this folder: Shared, Gateway, IdentityService, UserService, PropertyService, SearchService, BookingService, PaymentService, AgreementService, NotificationService, ChatService, ReviewService, WishlistService, MediaService, AnalyticsService, and AdminService.

The 15 service/gateway projects enforce the controller constructor dependency rule and each test the existing health controller's HTTP result, healthy status and service identity. These are direct controller unit tests: they do not start the application, connect to MySQL, or validate HTTP routing/authentication. The services currently have no business endpoints to test. Add tests beside each new feature as it is implemented; these initial tests do not imply full service coverage.

Shared includes database security and relational transaction tests, plus exception middleware, daily-file logging, sink failure handling and HTTP pipeline tests. Its MySQL integration tests remain opt-in; see [database test setup](../docs/database-access.md#verification).

## Running tests

Run all projects (also compiles their referenced service projects):

```sh
dotnet test EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Run only one service:

```sh
dotnet test tests/EasyTolet.UserService.Tests/EasyTolet.UserService.Tests.csproj --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Run the service/gateway tests tagged as unit tests:

```sh
dotnet test EasyToLet.sln --filter 'Category=Unit' --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

The pre-existing Shared tests are not tagged `Category=Unit`; use the full suite to include them. Existing MassTransit dependency resolution warnings may appear when building service references.

## Adding tests with future work

Use `Controllers`, `Application`, `Domain`, or feature folders as needed. Test business outcomes, validation failures, not-found/conflict cases, authorization decisions, and relevant boundary conditions. For bug fixes, add a regression test that exposes the previous behavior. Keep unit tests deterministic and independent of infrastructure; add separate integration coverage when correctness depends on EF/MySQL, transactions, HTTP middleware, or messaging. Do not introduce empty test files or always-passing placeholders for unimplemented features.

The persistent development requirement is recorded in [AGENTS.md](../AGENTS.md).

AdminService additionally tests Roles CRUD, hierarchy validation, outbox rollback/retry and signed-JWT HTTP endpoints. Optional MySQL and RabbitMQ tests are documented in [AdminService testing](../docs/admin-roles.md#tests-and-verification). The integration Contracts library is exercised by the Admin serialization and transport tests.

UserService registration/CRUD coverage: repository parameter mapping, validation, transaction boundaries, and TestServer routing/JWT authorization/error handling. Run `dotnet test tests/EasyTolet.UserService.Tests/EasyTolet.UserService.Tests.csproj`. The opt-in MySQL test requires `EASYTOLET_USER_TEST_MYSQL` (schema ending `_tests`) and `EASYTOLET_USER_TEST_ROLE_ID`, with the real Users/Roles tables and SP_User/SP_GetNextUserNumber/FN_FormatUserId installed. See `src/EasyTolet.UserService/README.md` for setup and semantics; skipped infrastructure tests do not establish database compatibility.

UserService SSO tests cover Google, Facebook, and Microsoft OAuth using the real ASP.NET Core handlers with fake server backchannels; they exercise callback state/correlation and replay rejection, CSRF, verified-identity registration, sessions, logout, deactivation, and failed-registration behavior. SSO repository tests cover exact identity lookup, parameter mapping, conflicts, and rollback. `SsoMySqlTests` is opt-in via the existing UserService test database variables and additionally requires `database/UserSsoIdentity.sql` for existing tables. Actual provider consent flows are not live-tested without configured provider apps.

Shared authorization tests cover pinned RSA JWT validation, strict expiry, fallback authorization, roles, and dynamic permission policies. UserService login tests cover PBKDF2 password handling, 20-minute JWT issuance, browser CSRF, SSO exchange, rate limits, rotation, logout, and failure handling. LoginSessionMySqlTests requires the updated SP_User/PasswordHash and UserLoginSessions.sql in the isolated UserService test schema; it verifies concurrent refresh, replay revocation, rollback, and password changes. See src/EasyTolet.UserService/AUTHENTICATION.md.

UserService account-activity tests cover self-service profile restrictions, authenticated session ownership, password change/recovery, single-use verification/deletion challenges, encrypted delivery outbox behavior, failure boundaries, HTTP routing/authentication and rate limiting. `AccountActivitiesMySqlTests` additionally requires `database/UserAccountActivities.sql` in the explicitly configured isolated UserService schema and tests actual SQL rollback, unique conflicts and concurrent reset consumption. See [account-activity setup](../src/EasyTolet.UserService/ACCOUNT-ACTIVITIES.md).

NotificationService now owns email/SMS/mobile-push delivery. Its tests cover templates, provider configuration, disabled channels, encrypted inbox deduplication, lease acknowledgments, retries and in-memory MassTransit transport. Optional real MySQL/RabbitMQ tests require the isolated notification test settings described in [NotificationService setup](../src/EasyTolet.NotificationService/README.md). UserService account tests verify broker commands and its existing transactional outbox; provider credentials no longer belong to UserService.

AdminService and UserService expose tested Swagger/OpenAPI documents with route-specific bearer/cookie security and UserService CSRF header requirements. Their TestServer documentation tests inspect the generated HTTP JSON; existing controller HTTP tests continue to verify route authorization and behavior. Direct local HTTPS URLs and runnable request examples are in [API endpoints](../docs/api-endpoints.md).

Gateway routing tests now exercise the real YARP middleware against in-process downstream servers for all 14 services. They cover authentication, forwarded bodies/queries/cookies, downstream permission failures, SSO redirects, spoofed forwarding headers and unavailable services. Shared trusted-gateway-header tests validate explicit proxy/host trust. See [gateway configuration](../src/EasyTolet.Gateway/README.md). These tests do not verify live deployment DNS/TLS or connect to databases/brokers.
