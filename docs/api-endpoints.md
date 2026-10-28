# Admin and User API endpoints

Use **https://localhost:7001** as the client base URL through EasyTolet.Gateway, and **https://localhost:7001/swagger** for Admin/User Swagger. See [gateway setup](../src/EasyTolet.Gateway/README.md).

Local HTTPS launch profiles also provide these direct service addresses for troubleshooting:

| Service | Base URL | Swagger UI | OpenAPI JSON |
| --- | --- | --- | --- |
| AdminService | `https://localhost:7015` | `https://localhost:7015/swagger` | `https://localhost:7015/swagger/v1/swagger.json` |
| UserService | `https://localhost:7003` | `https://localhost:7003/swagger` | `https://localhost:7003/swagger/v1/swagger.json` |

Start each service in its own terminal:

```sh
dotnet run --project src/EasyTolet.AdminService --launch-profile https
dotnet run --project src/EasyTolet.UserService --launch-profile https
```

Swagger is enabled only in the Development environment, before API authentication middleware so you can obtain a login token. Production API authorization remains enforced.

These profiles require a trusted ASP.NET Core development HTTPS certificate. Configure each service's database, RabbitMQ credentials and JWT settings first, and install its documented SQL routines/tables. UserService requires a signing private key; AdminService validates tokens using the matching public key/issuer/audience (or configured authority). These files do not contain real secrets or provision databases. NotificationService must also be configured/running for verification/reset messages to be delivered.

The request examples use the gateway. The URLs in the table above connect directly to the service APIs. Docker's existing HTTP port mappings are separate from these `dotnet run` HTTPS profiles; secure login cookies require an HTTPS deployment origin.

## AdminService

All role routes require a valid JWT with permission `admin.roles.manage` (`AdminRoles` policy). Writes additionally require a positive numeric `user_id` claim.

| Method | Path | Action |
| --- | --- | --- |
| GET | `/api/admin/roles?page=1&pageSize=20` | List roles |
| GET | `/api/admin/roles/{id}` | Get role |
| POST | `/api/admin/roles` | Create role |
| PUT | `/api/admin/roles/{id}` | Update role |
| DELETE | `/api/admin/roles/{id}` | Soft-delete role |

Create/update fields: `roleCode`, `roleName`, optional `parentRoleId`, optional `description`, and `isActive`. Role IDs must be positive integers. Creating a role does not automatically grant it to a user.

## UserService

| Method | Path | Authorization |
| --- | --- | --- |
| POST | `/api/users/register` | Anonymous registration |
| GET | `/api/users` | `users.manage` permission |
| GET | `/api/users/{id}` | Own identity or `users.manage` |
| PUT / DELETE | `/api/users/{id}` | `users.manage` and numeric `user_id` |
| GET | `/api/login/csrf` | Anonymous; creates CSRF cookie/token |
| POST | `/api/login` | Email/password plus CSRF token/cookie |
| POST | `/api/login/sso` | SSO session cookie plus CSRF |
| POST | `/api/login/refresh` | Login/refresh cookies plus CSRF |
| POST | `/api/login/logout` | Login session cookie plus CSRF |
| GET / PUT | `/api/account/profile` | Bearer JWT with active `user_id` / `sid` |
| POST | `/api/account/password/change` | Active account JWT and current password |
| POST | `/api/account/password/forgot` | Anonymous |
| POST | `/api/account/password/reset` | Anonymous; single-use recovery token |
| GET | `/api/account/verification` | Active account JWT |
| POST | `/api/account/verification/request` | Active account JWT |
| POST | `/api/account/verification/confirm` | Active account JWT and confirmation token |
| GET / DELETE | `/api/account/sessions` | Active account JWT |
| DELETE | `/api/account/sessions/{sessionId}` | Active account JWT; owned sessions only |
| POST | `/api/account/deletion/request` | Active account JWT |
| POST | `/api/account/deletion/confirm` | Active account JWT and emailed confirmation |
| GET | `/api/users/sso/providers` | Anonymous |
| GET | `/api/users/sso/{provider}/login` | Anonymous; starts browser OAuth redirect |
| GET | `/api/users/sso/complete` | External SSO cookie |
| POST | `/api/users/sso/register` | External SSO cookie plus CSRF |
| GET | `/api/users/sso/session` | SSO session cookie |
| POST | `/api/users/sso/logout` | SSO session cookie plus CSRF |

Each service also exposes anonymous `GET /api/health`.

Registration accepts a plaintext `password` over HTTPS; the application hashes it before calling `SP_User`. Never supply a hash in place of the API password. Registration's selected `roleId` is not an approved role grant and does not confer management permissions. See [UserService authentication](../src/EasyTolet.UserService/AUTHENTICATION.md) and [account activities](../src/EasyTolet.UserService/ACCOUNT-ACTIVITIES.md) for configuration and request semantics.

## Testing requests

Use [requests/admin.http](../requests/admin.http) and [requests/user.http](../requests/user.http) in an HTTP-request client, or use each service's Swagger UI. Replace sample IDs with test records and keep real credentials/tokens out of source control. Each request is independent; do not run destructive samples against real accounts/roles.

For password login, first call `/api/login/csrf`, retain its cookie and put the returned `csrfToken` in `X-CSRF-TOKEN`. Then call `/api/login` with `emailId` and `password`. Keep the response cookies and use the new CSRF token returned by login/refresh. Paste the returned access token into Swagger's **Authorize → Bearer** field for protected APIs. The UI adds `Authorization: Bearer ...` for bearer endpoints; anonymous endpoints remain anonymous.

Cookie-based operations are documented separately. Browser clients must preserve the secure HttpOnly login/SSO cookies; a bearer token alone cannot refresh a session. Swagger's cookie definitions document the protocol but cannot manually set HttpOnly cookies. For SSO, perform the provider redirect in the same browser/profile and use its latest CSRF token. When using the gateway, register provider callback URLs with its public origin (`https://localhost:7001/signin-google`, `/signin-microsoft`, or `/signin-facebook`). Start and finish the entire login flow through that same origin.

Existing HTTP tests verify authorization, invalid input and CRUD routing with fakes. New TestServer tests verify that actual generated OpenAPI paths/security/CSRF requirements are correct without starting MySQL, RabbitMQ or production applications. Passing these tests does not establish connectivity to your configured infrastructure.
