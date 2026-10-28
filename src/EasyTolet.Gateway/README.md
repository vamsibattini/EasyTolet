# EasyTolet API Gateway

The gateway uses YARP to forward HTTP requests to all 14 services. Client applications use **`https://localhost:7001`** in local development. User and Admin API paths are preserved. The gateway's database remains operational exception logging only; it never queries a downstream database.

## Start locally

Configure the service databases, RabbitMQ and JWT keys/authority as documented for each service. The gateway needs its own logging database credentials under `ExceptionLogging:Database` and the same JWT audience and trusted issuer/public key as the services. It does not need a private signing key. Trust your ASP.NET Core development HTTPS certificate before running HTTPS services; the proxy does not disable certificate validation.

Run these in separate terminals for User/Admin workflows:

```sh
dotnet run --project src/EasyTolet.UserService --launch-profile https
dotnet run --project src/EasyTolet.AdminService --launch-profile https
dotnet run --project src/EasyTolet.Gateway --launch-profile https
```

Start NotificationService too when testing account emails, and start any other services you want to call. Each service now has an `https` launch profile using the port below. Starting the gateway does not launch downstream services. A stopped/unreachable service returns a proxy error, normally `502` (timeout conditions can return `504`).

Open **`https://localhost:7001/swagger`** in Development. The document selector offers Admin and User APIs; both make requests through the gateway. Their JSON documents are proxied at `/docs/admin/swagger.json` and `/docs/user/swagger.json`. The downstream services must also run in Development to serve those documents. Production does not expose these anonymous documentation routes.

[User requests](../../requests/user.http), [Admin requests](../../requests/admin.http) and [Gateway requests](../../requests/gateway.http) use the gateway base URL. Keep cookies and the latest CSRF token in the same browser/client session for login, refresh and logout. Do not switch between direct-service and gateway origins during a login flow.

## Routes

| Service ID | Canonical API prefix | Local downstream | Health through gateway |
| --- | --- | --- | --- |
| identity | `/api/identity` | `https://localhost:7002` | `/health/identity` |
| user | `/api/users` | `https://localhost:7003` | `/health/user` |
| property | `/api/properties` | `https://localhost:7004` | `/health/property` |
| search | `/api/search` | `https://localhost:7005` | `/health/search` |
| booking | `/api/bookings` | `https://localhost:7006` | `/health/booking` |
| payment | `/api/payments` | `https://localhost:7007` | `/health/payment` |
| agreement | `/api/agreements` | `https://localhost:7008` | `/health/agreement` |
| notification | `/api/notifications` | `https://localhost:7009` | `/health/notification` |
| chat | `/api/chat` | `https://localhost:7010` | `/health/chat` |
| review | `/api/reviews` | `https://localhost:7011` | `/health/review` |
| wishlist | `/api/wishlist` | `https://localhost:7012` | `/health/wishlist` |
| media | `/api/media` | `https://localhost:7013` | `/health/media` |
| analytics | `/api/analytics` | `https://localhost:7014` | `/health/analytics` |
| admin | `/api/admin` | `https://localhost:7015` | `/health/admin` |

UserService additionally receives `/api/account/...`, `/api/login/...` and the OAuth callback paths `/signin-google`, `/signin-microsoft`, `/signin-facebook`. Existing calls such as `POST /api/users/register`, `POST /api/login`, `GET /api/account/profile`, and `GET /api/admin/roles` retain their original paths.

Every service also has an authenticated generic route: **`/services/{service-id}/{downstream-path}`**. For example `/services/user/api/users` forwards to UserService `/api/users`. Only configured service IDs are accepted; request values cannot choose a target host. Use the canonical `/api/login`, `/api/users/sso/...` and `/signin-*` paths for cookie/SSO flows, because generic aliases require a bearer JWT and redirect URLs use canonical paths.

`/api/health` is the gateway's own health controller. `/health/{service-id}` forwards to the corresponding downstream `/api/health` and is anonymous, matching the existing health endpoints. Most services currently implement health endpoints only; adding a gateway prefix does not create booking/property/payment business APIs. Notification sending remains an internal RabbitMQ operation, not a public HTTP send endpoint.

## Authentication and forwarding

Protected routes require a bearer token at the gateway. The original Authorization header is forwarded and each downstream service independently authenticates and enforces its permissions. The gateway does not invent role claims or trust identity headers from clients.

Explicit registration and password-recovery endpoints pass through anonymously. `/api/login/...` and `/api/users/sso/...` pass through without a gateway JWT requirement because UserService owns their cookie authentication, rate limiting, CSRF and SSO identity checks. OAuth callbacks also pass through to its authentication middleware. These gateway exceptions do not disable downstream authorization.

YARP forwards request methods, body, query, Cookie/Set-Cookie and response status/Location. It replaces client-supplied X-Forwarded values and removes the untrusted `Forwarded` header. Every service processes forwarding headers only from explicitly trusted proxy IPs and only for configured public host names. This preserves the public HTTPS origin for SSO callbacks and redirects, and the original client IP for rate limiting.

The Development files trust loopback gateway connections and allow `localhost`, `127.0.0.1` and `[::1]`. In production, configure the following **on every downstream service**:

```json
{
  "GatewayHeaders": {
    "KnownProxies": ["10.20.0.10"],
    "AllowedHosts": ["api.example.com"]
  }
}
```

Use the actual gateway peer IP seen by that service (IPv4/IPv6 as appropriate), not this example IP. Empty trust configuration ignores forwarded headers. Wildcard public hosts are rejected. The forwarding limit is one hop. If the gateway itself is behind a trusted TLS-terminating ingress, configure its `GatewayHeaders` with that ingress peer IP/public host too. Do not enable unrestricted forwarded-header trust.

Configure the gateway's **`AllowedHosts`** for its public domain (default is localhost/loopback). Register OAuth provider callbacks against that same gateway origin, for example `https://localhost:7001/signin-google` locally or `https://api.example.com/signin-google` in production. Do not use the downstream service's internal address for callbacks.

## Deployment destinations

`appsettings.Development.json` supplies the local HTTPS ports. `appsettings.json` supplies internal service-DNS HTTP origins such as `http://easytolet.userservice:80/`. Override a destination with an environment variable:

```text
DownstreamServices__user__Address=https://user.internal.example/
DownstreamServices__admin__Address=https://admin.internal.example/
```

Restart the gateway after changing destinations. Origins must be HTTP/HTTPS with no embedded credentials, path, query or fragment. TLS certificate validation stays enabled. Production internal HTTP is appropriate only on a protected private network; use HTTPS if that boundary is not private. Expose only the gateway publicly, keep downstream services private, and terminate external traffic with HTTPS because login uses Secure cookies. Existing Docker HTTP port mappings do not themselves provide TLS; use a configured ingress or gateway certificate when deploying containers. Forwarding trust must match the container/ingress network, not loopback.

Gateway changes do not modify business repositories, databases, RabbitMQ notification flow or existing JWT expiration/revocation behavior.

## Verification

```sh
dotnet build EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
dotnet test EasyToLet.sln --disable-build-servers -m:1 /p:UseSharedCompilation=false
```

Gateway tests exercise the real YARP and ASP.NET Core pipelines against in-process downstream HTTP servers, including all service routing, method/body/query preservation, token rejection, downstream permissions, cookie/CSRF forwarding, public-origin redirects, header spoofing prevention and unavailable-backend responses. Shared tests verify trusted-proxy and host restrictions. These tests need no MySQL/RabbitMQ credentials and do not send real OAuth requests. They do not establish connectivity, DNS or certificate trust for your live deployment.

References: [YARP authentication](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/authn-authz) and [request transforms](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/yarp/transforms-request).
