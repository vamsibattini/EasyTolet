# User registration and CRUD

`UsersController` injects UserService's `AppDbContext`, `IExceptionReporter`, and `IUserBusinessRepository`. `UserBusinessRepository` calls `SP_User` through the shared `ISqlExecutor`, using `IUnitOfWork` transactions for writes. The global exception middleware reports failures once. Email/password login, SSO-to-JWT exchange, and shared authorization are described in [AUTHENTICATION.md](AUTHENTICATION.md).

| Method | Route | Access | Procedure action |
| --- | --- | --- | --- |
| POST | `/api/users/register` | Anonymous | I, then R |
| GET | `/api/users?userId=UETID_00000001` | `users.manage` permission | R |
| GET | `/api/users/{id}` | Own numeric `user_id` claim or `users.manage` | R |
| PUT | `/api/users/{id}` | `users.manage` and positive numeric `user_id` | U, then R |
| DELETE | `/api/users/{id}` | `users.manage` and positive numeric `user_id` | D (soft delete) |

Registration example:

```json
{
  "roleId": 2,
  "password": "Example-password-123!",
  "firstName": "Vamshi",
  "lastName": "Bathini",
  "address1": "123 Main Road",
  "country": "India",
  "state": "Telangana",
  "city": "Hyderabad",
  "pinNumber": "500001",
  "mobileNumber": "9999999999",
  "emailId": "vamshi@example.com"
}
```

Use the actual RoleId selected in the dropdown (2 is illustrative). As requested, registration accepts any existing role. Role selection is not proof of permission: the trusted identity issuer must govern privileged claims. Audit fields and generated UserId are server-owned. SSO fields are excluded from request bodies and ordinary profile responses. The verified provider flow below supplies them during SSO registration.

PUT accepts the same required profile fields plus `isActive` (default true). Optional fields include `lastName`, `address2`, `address3`, and `alternateMobileNumber`. The supplied procedure clears nullable addresses/alternate mobile when null, but preserves LastName and SSO fields when null. The nullable PasswordHash column stores application-generated hashes; optional password updates accept `password`, never a client-supplied hash. Delete preserves the row and audit history; administrative reads include inactive users. Listing follows the supplied unpaginated procedure; use the optional exact UserId filter when possible.

Validation returns 400, missing users 404, database uniqueness/foreign-key/concurrency conflicts 409, and unexpected errors 500. Writes obtain audit identity from the validated JWT `user_id` claim, never the request body. The supplied insert procedure sets CreatedBy to the newly generated numeric Id.

## Database setup

Configure UserService's existing `Database` connection/password and `Jwt:Authority` / `Jwt:Audience` settings. Database scripts are supplied for review/manual installation, not run automatically:

1. Provision the local `Roles` table and role data in the UserService database. The supplied SP_User joins it and Users has a foreign key to it. Do not query AdminService's database.
2. Install `database/Users.sql` for a new table. For an existing table, apply `database/UserPassword.sql` once to add PasswordHash.
3. Provision `UserIdSequence` using InnoDB, with a unique SequenceId and an initialized row for SequenceId = 1. Initialize LastNumber to the last issued number on an existing deployment. Install `database/SP_GetNextUserNumber.sql` and the actual `FN_FormatUserId` definition. The supplied sequence routine assumes its row exists. Neither routine may commit the caller's transaction or perform DDL.
4. For an existing table, apply `database/UserSsoIdentity.sql` before enabling SSO. New Users.sql already includes this unique key. Resolve legacy duplicate identities explicitly before migration.
5. Replace the existing routine with the reviewed `database/SP_User.sql` during deployment. It now has 23 parameters, including the final p_PasswordHash input; deploy the application and routine together. It does not expose hashes in SELECT results.
6. Apply `database/UserLoginSessions.sql` after Users/Roles exist to enable JWT login and trusted role grants; see [AUTHENTICATION.md](AUTHENTICATION.md) for signing keys and permission setup. Scripts are not run automatically.


Role replication/provisioning, the sequence table, and the remaining `FN_FormatUserId` definition are prerequisites. The service cannot successfully register users until these exist. The API assumes InnoDB and transactional routines; the opt-in test checks rollback after insert.

## Verification

Run `dotnet test tests/EasyTolet.UserService.Tests/EasyTolet.UserService.Tests.csproj --disable-build-servers -m:1 /p:UseSharedCompilation=false`.

HTTP pipeline tests use TestServer with signed test JWTs and a fake repository; repository unit tests need no infrastructure. To exercise MySQL, explicitly set `EASYTOLET_USER_TEST_MYSQL` to a dedicated, pre-provisioned schema ending `_tests` and `EASYTOLET_USER_TEST_ROLE_ID` to an existing role in that schema. Install the actual tables and all three routines there first. The integration test uses unique email addresses, verifies CRUD, duplicate conflicts, audit fields, and user/sequence transaction rollback, and removes only its test users. ID sequence values may advance. Without both settings it is reported as skipped.

## Google, Facebook, and Microsoft SSO

SSO uses the official ASP.NET Core OAuth handlers. Google covers Google/Gmail accounts; Microsoft covers the account types allowed by your app registration, including Outlook. Instagram is excluded as requested.

`SsoController` injects the service context and exception reporter like the other controllers. The browser follows a provider authorization-code flow with protected state and correlation cookies. Google and Microsoft use PKCE. Code exchange and profile retrieval happen on the server. Only a successful callback can issue the protected, ten-minute external cookie used for onboarding. Do not send provider IDs, emails, profile JSON, or access tokens in request bodies as authentication proof.

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/api/users/sso/providers` | List configured, enabled provider names |
| GET | `/api/users/sso/google/login` | Start Google sign-in |
| GET | `/api/users/sso/facebook/login` | Start Facebook sign-in |
| GET | `/api/users/sso/microsoft/login` | Start Microsoft sign-in |
| GET | `/api/users/sso/complete` | Fixed destination after OAuth callback; sign in an existing user or return onboarding details |
| POST | `/api/users/sso/register` | Complete the required profile and register the verified external identity |
| GET | `/api/users/sso/session` | Return the signed-in profile and a CSRF token; recheck the active account in the database |
| POST | `/api/users/sso/logout` | Clear local SSO cookies |

Use a top-level browser navigation for `/login`, not an AJAX request that follows provider redirects. The three provider callback URLs are `/signin-google`, `/signin-facebook`, and `/signin-microsoft`; these are handled by middleware, not controllers. No user-supplied return URL is accepted.

A new identity receives a JSON response from `/complete`:

```json
{
  "registrationRequired": true,
  "provider": "Google",
  "displayName": "Provider Name",
  "suggestedEmail": "person@example.com",
  "csrfToken": "opaque-token"
}
```

Send the usual registration JSON (including the selected `roleId`, address, phone, and contact email) to `/sso/register`, with the browser cookies and `X-CSRF-TOKEN` header containing that token. A provider may not return email; the contact email must still be supplied, and it is not treated as verified merely because it was entered. The server obtains `SSOProvider`, `SSOProviderUserId`, and `SSOEmailId` from the protected provider identity. `SSOProfileData` stores only provider/display-name metadata. Provider access/refresh tokens are neither persisted nor returned to the caller.

Both returning-user completion and successful registration return `{ "registrationRequired": false, "user": { ... }, "csrfToken": "..." }` and issue a Secure, HttpOnly, nonpersistent session cookie with an eight-hour ticket lifetime. Use the newly returned CSRF token for logout. All SSO responses disable caching. Session and onboarding are designed for HTTPS on the same origin as the frontend; the antiforgery cookie uses SameSite=Strict and authentication cookies use SameSite=Lax.

The provider session is local to UserService. Exchange it at `POST /api/login/sso` for a 20-minute JWT and rotating refresh session, as described in [AUTHENTICATION.md](AUTHENTICATION.md). Approved server-side grants determine JWT roles/permissions; selecting a role at signup does not approve it. Configure the matching RSA public key and issuer throughout the services/gateway.

Registration calls `SP_User` with action I, all four SSO parameters, and the selected role. Insert, sequence increment, audit changes, and full-profile read share one transaction; the session is issued only after commit. Login matches the exact provider/subject pair, never email. Existing email conflicts and duplicate identity registrations return 409; inactive users return 403. An existing email is not automatically linked or overwritten. The current Users schema supports one provider identity per user row; linking multiple providers to a single account would require a separate identity table and an authenticated linking flow.

### Provider configuration

Providers are disabled by default. Configure credentials through user secrets, deployment secrets, or environment variables, not committed configuration:

```text
Authentication__Google__Enabled=true
Authentication__Google__ClientId=<Google OAuth client ID>
Authentication__Google__ClientSecret=<Google OAuth client secret>
Authentication__Facebook__Enabled=true
Authentication__Facebook__ClientId=<Meta app ID>
Authentication__Facebook__ClientSecret=<Meta app secret>
Authentication__Facebook__GraphApiVersion=<supported version for your Meta app, e.g. vNN.0>
Authentication__Microsoft__Enabled=true
Authentication__Microsoft__ClientId=<Microsoft application client ID>
Authentication__Microsoft__ClientSecret=<Microsoft client secret value>
```

Register each exact public HTTPS callback URL in its provider console. Choose your Microsoft supported account types explicitly. For Facebook, configure Facebook Login and the app's supported Graph API version; Meta review/permissions and app mode determine who can sign in. The API version is intentionally required rather than relying on the package's older default. All providers need their own registered app/credentials.

When deployed behind a gateway/proxy, route both `/api/users/sso/*` and the three callback paths to UserService and preserve the external HTTPS scheme/host. Configure forwarded headers only for trusted proxies and restrict allowed hosts. Persist and protect ASP.NET Core Data Protection keys, sharing them across UserService replicas so OAuth state and cookies remain valid across instances and restarts. Do not log callback query strings, authorization codes, provider secrets, or cookies.

Setup references: [Google](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/google-logins?view=aspnetcore-9.0), [Facebook](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/facebook-logins?view=aspnetcore-9.0), [Microsoft](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/social/microsoft-logins?view=aspnetcore-9.0).

### SSO tests

`SsoHttpTests` exercises actual provider handlers, protected state, correlation cookies, registration, sessions, CSRF protection, callback replay, forged callbacks, provider failures, deactivation, and logout through TestServer. Provider backchannels and repositories are faked; no credentials or external calls are required. Repository tests cover trusted parameter mapping, conflicts, disabled identities, and rollback. The opt-in `SsoMySqlTests` requires the same isolated MySQL configuration as the CRUD test plus the SSO uniqueness migration, and verifies persisted SSO fields, case-sensitive subjects, unique constraints, email conflicts, and soft-delete rejection. Live provider-console/consent flows require your credentials and are not verified by these simulated tests.

Self-service profile, recovery, verification, account deletion and session management are documented in [account activities](ACCOUNT-ACTIVITIES.md), including the additional schema and message-delivery configuration.

Local HTTPS launch URL, Swagger authentication and HTTP request examples: [API endpoints](../../docs/api-endpoints.md).
