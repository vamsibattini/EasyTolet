# Password login, JWTs, and shared authorization

UserService owns `LoginController` because it owns the Users credentials and existing SSO identities. No service or gateway reads another service's database. `EasyTolet.Shared.Security` supplies the same authentication and authorization pipeline to all services and the gateway. Token issuance/rotation belongs to the login application service; middleware validates the presented JWT and enforces controller policies. Protected requests never silently mint a token.

## Password column and SP_User changes

For an existing table run `database/UserPassword.sql` once:

```sql
ALTER TABLE Users
ADD COLUMN PasswordHash varchar(512) DEFAULT NULL AFTER EmailId;
```

Store a hash, never `Password` plaintext. New `database/Users.sql` already contains this column; do not apply the ALTER twice. Existing/Sso-only users keep NULL, which disables password login until a password is set through a trusted flow. Do not copy plaintext passwords into this column or use SQL MD5/SHA functions to hash passwords.

`database/SP_User.sql` is already updated. Make these matching changes when replacing the existing routine:

1. Add the final input parameter, after `p_ActionBy` (including the comma):

```sql
IN p_ActionBy BIGINT,
IN p_PasswordHash VARCHAR(512)
```

2. In the I branch, add `PasswordHash` immediately after `EmailId` in the INSERT column list, and `p_PasswordHash` immediately after `p_EmailId` in the matching VALUES list.
3. In the U branch's SET clause add:

```sql
PasswordHash = COALESCE(p_PasswordHash, PasswordHash),
```

4. Do not add PasswordHash to any SELECT result. R and D otherwise remain unchanged.

All calls now supply 23 parameters. Coordinate deployment of the application and the replaced routine; MySQL procedure parameters cannot be added with a default that preserves the old call signature. Back up the current routine definition and replace it with the reviewed updated file during deployment. Stored-routine DDL is not part of a registration transaction.

Ordinary registration requires a 12–128-character `password`. SSO registration may omit it. Optional password changes on the existing authorized PUT user endpoint are hashed in the application; null preserves the old hash. Passwords are not trimmed. ASP.NET Core PasswordHasher stores a versioned PBKDF2 hash with a random salt/work factor. The value sent to `p_PasswordHash` is the generated hash only. Public profile responses never include it. This change does not add email verification or a self-service forgotten-password/reset flow.

## Database session and permission tables

Apply `database/UserLoginSessions.sql` once in the UserService database, after Users/Roles. It creates:

- `UserLoginSessions`: eight-hour absolute session expiry, last successful refresh time, revocation, user binding, and credential fingerprint.
- `UserRefreshTokens`: SHA-256 hashes of random 64-byte tokens; old hashes remain to detect replay. Raw refresh tokens are not stored.
- `UserRoleGrants`: trusted approval of a user's selected role. The approved RoleId must still match Users.RoleId.
- `RolePermissions`: trusted, case-sensitive permission strings for each role.

Public registration accepts the selected RoleId but never approves it. A user without an approved grant can sign in and use authenticated endpoints; they have no JWT role or permission claims. Provision/approve grants with trusted administration, not public signup requests. For example, using reviewed parameter values in a trusted SQL session:

```sql
INSERT INTO UserRoleGrants (UserId, RoleId, ApprovedBy)
VALUES (@UserId, @ApprovedRoleId, @ApprovingUserId);
INSERT INTO RolePermissions (RoleId, Permission)
VALUES (@ApprovedRoleId, 'users.manage');
```

JWT role claims use the local Roles.RoleCode (e.g. ADMIN), not role display names or client-supplied claims. Only active roles and users are eligible. Existing role replication/provisioning prerequisites still apply. Changing a user's selected role requires approval of that role before it grants access. Multiple permissions can be stored per role. Permission/grant management UI and cross-service role replication are not added here.

## Signing and validation configuration

Provision an RSA key pair of at least 2048 bits using your deployment secret/certificate management. Do not commit the private key. Configure:

| Setting | UserService issuer | Every validating service/gateway |
| --- | --- | --- |
| `Jwt:Issuer` | Same issuer identifier, e.g. your public UserService login origin | Exact same value |
| `Jwt:Audience` | `EasyTolet.Api` | `EasyTolet.Api` |
| `Jwt:SigningPrivateKeyPem` | Private RSA PEM, secret storage only | Never distribute |
| `Jwt:PublicKeyPem` | Matching public RSA PEM | Matching public RSA PEM |
| `Jwt:KeyId` | Optional identifier; default easytolet-v1 | Public key is explicitly pinned |

The appsettings audiences now use `EasyTolet.Api` throughout. The public-key configuration validates signature/RS256, issuer, audience, lifetime, and expiration with zero clock skew. Keep host clocks synchronized. An HTTPS `Jwt:Authority` discovery endpoint remains supported when no public key is configured; it must advertise the actual issuer's public keys. The locally implemented issuer does not publish OIDC discovery, so configure the matching public PEM for this flow. Configure key rotation through deployment; this initial pinned-key setup does not provide a key-management API.

All services use `AddServiceSecurity(configuration)` and `UseServiceSecurity()`. Unannotated endpoints require authentication through the fallback policy. Health endpoints and intended registration/SSO/login bootstrap routes are explicitly anonymous. The gateway has the same validator but still needs actual proxy routes if it is to forward calls; gateway authentication does not replace downstream validation.

## Browser login and refresh

Use HTTPS and same-origin requests. Browser session cookies are Secure/HttpOnly, and refresh/CSRF cookies use SameSite=Strict. Access tokens are returned as JSON and should be kept in memory rather than persisted in browser storage.

1. `GET /api/login/csrf` to obtain `{ "csrfToken": "..." }` and its antiforgery cookie.
2. `POST /api/login`, including cookies and header `X-CSRF-TOKEN`, with:

```json
{"emailId":"person@example.com","password":"your-password"}
```

3. The response contains `accessToken`, `tokenType: "Bearer"`, `expiresIn: 1200`, `expiresAtUtc`, and a new `csrfToken`. The server sets a protected browser-session cookie and a separate opaque refresh cookie. Raw refresh tokens do not appear in JSON.
4. Send `Authorization: Bearer <accessToken>` to protected service endpoints. Roles/permissions are copied only from approved server data.
5. Before expiry while the user is active, or after an expired-token 401, `POST /api/login/refresh` with cookies and the latest CSRF header. No valid access token is needed for this endpoint; the protected login cookie plus the current refresh token are required. Store the replacement access token and CSRF token. The refresh cookie rotates automatically.
6. `POST /api/login/logout` with cookies and CSRF header to revoke the refresh family and clear login/SSO cookies.

For SSO, finish the existing provider flow, obtain its CSRF token from `/api/users/sso/session`, then `POST /api/login/sso` with the SSO cookie and CSRF header. It issues the same JWT/refresh session. `/api/users/sso/logout` only handles the original SSO session; use `/api/login/logout` to end a JWT refresh session.

The client controls renewal while the user remains active. Middleware does not extend expiry merely because a tab is open. Refresh must occur within 30 minutes of login/last successful refresh and before the eight-hour absolute expiry; refreshing does not extend the absolute limit. Browsers may restore nonpersistent cookies on restart, so server expiry/revocation—not browser-close detection—is authoritative. Client code should serialize refresh requests across concurrent requests/tabs. Reusing an already consumed token revokes the entire session family, including a replacement token issued to another concurrent refresh. On 401 from refresh, stop retrying and require login again.

Account deactivation, password changes, logout, and replay prevent subsequent refresh. Role/permission changes are reloaded on refresh. Already-issued JWTs remain valid until their 20-minute expiry because downstream validation is stateless; immediate global access-token revocation would require an online session check or distributed revocation mechanism. Do not promise immediate JWT invalidation on logout.

Session creation and refresh rotation use the shared UnitOfWork transaction. Rotation locks the session family before inspecting consumption; it commits replay revocation before returning an error. JWT signing occurs before rotation so signing failures do not consume a usable refresh token. Data failures do not return successful token responses. Consumed token hashes should be retained until the session expires; expired session families can be deleted with their child rows by an operational cleanup job. No automatic production cleanup job is installed.

Password login and SSO-to-JWT exchange are limited to 10 attempts/minute/IP per process. Configure trusted forwarded headers at deployment; use a gateway/distributed limiter when running multiple replicas. Keep Data Protection keys persistent/shared across UserService replicas. No private keys, passwords, tokens, or cookies should be logged.

## Controller usage

```csharp
using EasyTolet.Shared.Security;
using EasyTolet.Shared.Controllers;
using EasyTolet.Shared.Exceptions;
using Microsoft.AspNetCore.Authorization;

[Authorize(Roles = "ADMIN")]
[RequirePermission("users.manage")]
public sealed class ExampleController(AppDbContext database, IExceptionReporter reporter)
    : ServiceControllerBase<AppDbContext>(database, reporter)
{
    // Add the protected actions here.
}
```

Place attributes on controllers or actions. Role plus permission means both are required. `[Authorize(Roles = "ADMIN,OWNER")]` accepts either role; separate attributes require all policies. `[Authorize]` only requires a valid identity. `[AllowAnonymous]` intentionally bypasses authorization. Names are case-sensitive. Existing AdminRoles/ManageUsers policies remain supported.

## Verification

Commands:

```sh
dotnet build EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
dotnet test EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Shared TestServer tests exercise signature/issuer/audience/expiry failures, fallback protection, roles, and permissions. UserService tests exercise password hashing/validation, approved claims, 20-minute JWTs, SSO exchange, CSRF, refresh after JWT expiry, rotation, logout, rate limiting, and failed signing/persistence. The opt-in MySQL test verifies real rollback, concurrent refresh/replay, idle expiry, and password-change invalidation. Configure the existing isolated UserService `_tests` database variables, and install the updated routine, PasswordHash column, and login/session/grant tables before running it. Skipped MySQL tests and simulated provider tests do not establish live database/provider compatibility.
