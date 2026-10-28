# Self-service account activities

These endpoints extend registration, login and administrative user management. They do not replace `api/users` or its `users.manage` permission. All private routes use a Bearer JWT containing `user_id` and `sid`; they never accept a target user ID from a request. Each operation checks that the account and login session are active, unexpired, within the 30-minute refresh inactivity window, and match the current password stamp. Cookies alone cannot authenticate these routes, so cookie CSRF credentials are not accepted here.

## API

| Method and path | Request / result |
| --- | --- |
| `GET /api/account/profile` | Current user's profile |
| `PUT /api/account/profile` | Safe profile fields below; `204` |
| `POST /api/account/password/change` | `currentPassword`, `newPassword`; `204`, sign in again |
| `POST /api/account/password/forgot` | Anonymous; `email`; uniform `202` response |
| `POST /api/account/password/reset` | Anonymous; `token`, `newPassword`; `204`, sign in again |
| `GET /api/account/verification` | `emailVerified`, `phoneVerified` for the current contact values |
| `POST /api/account/verification/request` | `channel`: `email` or `phone`; optional `destination`; `202` |
| `POST /api/account/verification/confirm` | `token`; `204`; sign in again if contact changed |
| `POST /api/account/deletion/request` | No body; sends confirmation to current email; `202` |
| `POST /api/account/deletion/confirm` | `token`; `204`; account is deactivated |
| `GET /api/account/sessions` | Active session IDs, creation/activity/expiry times and `isCurrent` |
| `DELETE /api/account/sessions/{sessionId}` | Revokes only an owned session; idempotent `204` |
| `DELETE /api/account/sessions` | Revokes all owned sessions, including current; `204` |

Profile updates require `firstName`, `address1`, `country`, `state`, `city`, and `pinNumber`. Optional fields are `lastName`, `address2`, `address3`, and `alternateMobileNumber`; omitted optional fields are cleared. Privileged, unknown and contact-identity fields (such as `roleId`, `isActive`, `password`, `emailId`, `mobileNumber`, `userId`) are rejected. The main email/phone can be changed only through verification. Phone destinations must use international format such as `+919876543210`; alternate phone numbers are profile data and are not marked verified.

New passwords follow the existing 12–128 character requirement. Changing a password requires the current password. Password recovery is available only for active password-based accounts; SSO-only users recover access through their provider. Email/phone verification is recorded but does not newly block existing login flows or automatically grant roles/permissions.

## Confirmation and session behavior

Confirmation tokens contain 256 random bits, expire in 15 minutes, are single-use and purpose-bound, and are stored as SHA-256 hashes. They are also bound to the account's password stamp and original contact. Verifying another user's token, reusing one, or confirming after the original contact/password changes fails. Requests serialize on the user row and allow one message per user/purpose per minute; repeated requests return the normal response. The existing `Login` rate limiter additionally allows ten account/login requests per remote IP per minute, per application replica. Configure ingress-level limits when running multiple replicas; proxy IP forwarding must be trusted and configured before using client IPs.

Password change/reset, contact changes and deletion atomically revoke every refresh session and invalidate outstanding challenges. Session lists omit expired, idle, revoked or credential-stale sessions. Deletion is **soft deletion**, matching the existing service: it retains profile/audit data, marks `IsActive=false`, and blocks further login/account operations. It is not data erasure.

New account routes check session state immediately. Already-issued JWTs remain cryptographically valid for their existing 20-minute lifetime at other service endpoints that only use the shared JWT validator. This change does not implement cross-service token introspection or a distributed revocation list. Revocation also does not sign a user out of their external Google/Microsoft/Facebook identity provider; an active user can deliberately sign in again through SSO.

## Database and broker setup

1. Apply `database/UserAccountActivities.sql` to the UserService database after the existing user/session tables.
2. Configure `RabbitMq:Address` (or the existing `RabbitMq:Host`), `RabbitMq:Username` and `RabbitMq:Password`. UserService publishes committed account-outbox messages to NotificationService's durable queue. It does not contact email, SMS or push providers.
3. Persist `AccountOutbox:DataProtectionKeyDirectory` on a private volume shared by UserService replicas. This replaces the old `AccountDelivery:DataProtectionKeyDirectory` configuration key without changing the Data Protection application name or encrypted payload purpose. Reuse the existing UserService key directory so pending messages remain readable.
4. Keep `AccountFeatures:PhoneVerificationEnabled=false` until SMS delivery is configured in NotificationService. Disabled requests return `503` without creating challenges.
5. Follow [NotificationService setup](../EasyTolet.NotificationService/README.md) for its database schema, Gmail secret, SMS/push adapters, inbox retries and separate key ring. Move any earlier SMTP secret from UserService to NotificationService as `Notifications:Email:Password`.

The account transaction still commits the hashed challenge and encrypted outbox together. The outbox worker now publishes a shared `NotificationRequestedV1` command through RabbitMQ. The stable command ID allows NotificationService to deduplicate retries. `UserAccountMessages.DeliveredAtUtc` retains its historical column name but now means broker acceptance, not provider delivery. Public `202` responses indicate queued work only.

Email templates, SMTP, SMS and mobile push are owned exclusively by NotificationService. Confirmation tokens are never returned by the account API. They remain 43-character, single-use tokens (including phone verification); no frontend screens are included. UserService is always responsible for checking expiry and rejecting consumed/invalidated tokens, even when an already-published message is delivered later.

## Verification

```sh
dotnet test tests/EasyTolet.UserService.Tests/EasyTolet.UserService.Tests.csproj --disable-build-servers -m:1 /p:UseSharedCompilation=false
dotnet build EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Unit and TestServer tests cover input validation, privilege-field rejection, authentication, session ownership, token purpose/expiry/replay/stamp checks, password verification, encryption, queued delivery, failure/commit boundaries and rate limiting. UserService tests verify stable broker commands and outbox failure boundaries. NotificationService tests own provider transport, template, inbox and consumer verification. Live delivery requires configured providers.

`AccountActivitiesMySqlTests` requires `EASYTOLET_USER_TEST_MYSQL` (database name ending `_tests`), `EASYTOLET_USER_TEST_ROLE_ID`, the existing UserService routines/tables and `UserAccountActivities.sql`. It exercises the real provider's profile updates, ownership, unique email conflict rollback, queue/password rollback, contact verification, concurrent single-use reset and soft deletion. Without explicit configuration it is skipped; passing unit tests does not establish MySQL or live delivery compatibility.
