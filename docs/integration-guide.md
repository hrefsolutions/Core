# Href.Core integration guide

This guide is for teams that connect a **satellite application** (its backend) or the **admin frontend** to Href.Core.

The interactive reference lists every endpoint, request and response:

- `/scalar` (Scalar UI, with "Try it");
- `/openapi/v1.json` (the OpenAPI document).

Both are anonymous. They are on by default and controlled by `OpenApi__Enabled`. The reference is the contract. This guide explains the flows and the rules that the reference cannot show.

Contents:

1. [Register an application and keep its secret](#1-register-an-application-and-keep-its-secret)
2. [Machine-to-machine (M2M) headers](#2-machine-to-machine-m2m-headers)
3. [Send emails](#3-send-emails)
4. [Log users in from your backend](#4-log-users-in-from-your-backend)
5. [Account flows and the pages your app must host](#5-account-flows-and-the-pages-your-app-must-host)
6. [Validate Core access tokens in a .NET satellite](#6-validate-core-access-tokens-in-a-net-satellite)
7. [Errors and rate limits](#7-errors-and-rate-limits)

## 1. Register an application and keep its secret

A Core administrator registers each satellite application. The admin endpoints need a bearer access token issued for the reserved `core` application to a user with the `core.admin` permission. The admin frontend gets one through `POST /api/auth/login`, called with the `core` M2M credentials (see section 4).

1. `POST /api/apps` with `{ "appKey": "billing", "name": "Billing", "description": "…", "allowSelfRegistration": false }`.
   - `appKey` is a lowercase slug of 3 to 50 characters: letters, digits and single hyphens, for example `billing` or `field-ops`. It never changes. It identifies your application everywhere: in the M2M headers, as the `aud` claim of your users' tokens, and as `app_key` in the audit log.
   - `allowSelfRegistration: true` enables `POST /api/auth/register` for this application (section 5).
2. The `201` response contains `clientSecret`. **It is shown only this time.** Store it in your backend's secret store, for example a Railway variable. Never put it in a browser, a mobile app or a repository.
3. If the secret leaks, an admin calls `POST /api/apps/{appKey}/secret/rotate`. The new secret is returned once, and **the previous one stops working immediately**. Deploy the new secret right after rotating.
4. An admin also defines what your users can do in your application:
   - permissions: `/api/apps/{appKey}/permissions`, keys such as `orders.read`;
   - roles: `/api/apps/{appKey}/roles`, sets of permissions, one of them optionally the default role for self-registered users;
   - user access: `PUT /api/apps/{appKey}/users/{userId}/roles`.

   A user can log in to your application only when they have access to it.

An admin can suspend an application with `PATCH /api/apps/{appKey}/status`. From then on, every M2M call of that application returns `403 Auth.ApplicationSuspended`.

## 2. Machine-to-machine (M2M) headers

Every call from your backend to the M2M endpoints carries both headers:

| Header | Value |
|---|---|
| `X-App-Key` | Your `appKey`. |
| `X-App-Secret` | Your `clientSecret`. |

The M2M endpoints are `GET /api/apps/me`, `/api/notifications` and `/api/auth/*`. To check the credentials, call `GET /api/apps/me`, which returns your application. Missing or wrong credentials return `401 Auth.InvalidAppCredentials`.

Optional headers:

- **`X-End-User-Ip`**: the IP address of **your** end user, exactly one IPv4 or IPv6 address.
  - The Core uses it for the auth rate limits (section 7) and records it in the audit log.
  - **Send it in production on every auth call.** Without it, all your users share one rate limit budget, the one of your server's IP.
  - It is used only on M2M calls. An invalid value is ignored and the connection address is used instead.
- **`X-Correlation-Id`**: up to 64 characters among `A-Z`, `a-z`, `0-9`, `-` and `_`.
  - The Core returns it in the response header and in error bodies, and stores it in the audit log and on queued emails.
  - Without it, or with an invalid value, the Core generates one.
  - Send your own request id to follow a request across both systems.

The admin endpoints do not accept M2M credentials, and the M2M endpoints do not accept admin tokens.

## 3. Send emails

`POST /api/notifications/email` queues one email. The Core sends it in the background, with up to 3 delivery attempts in total. Use exactly one of two modes.

**Template mode**, using a template that an admin manages under `/api/notifications/templates`:

```json
{
  "to": { "email": "ana@example.com", "name": "Ana Pérez" },
  "templateKey": "welcome-user",
  "variables": { "UserName": "Ana", "AppName": "Billing" },
  "notifyOnFailureTo": "ops@example.com"
}
```

- The key is exact and case-sensitive.
- Every variable the template uses must be in `variables`, otherwise you get `400 Templates.MissingVariables`. Values are HTML-encoded in the HTML body.
- An unknown key returns `404 Templates.NotFound`.

**HTML mode**, with content you render yourself:

```json
{
  "to": { "email": "ana@example.com" },
  "subject": "Your invoice",
  "html": "<p>Hello Ana</p>",
  "text": "Hello Ana"
}
```

- `html` and `text` are limited to 512 KB each, measured as UTF-8 bytes.
- Never mix the two modes: that returns `400 Notifications.InvalidContent`.

The response is `202` with `{ "id", "status": "Pending" }` and a `Location` header. Follow the delivery with:

- `GET /api/notifications/{id}`: status `Pending`, `Processing`, `Sent` or `Failed`, plus the number of attempts and `lastError`;
- `GET /api/notifications`: your notifications, paged, newest first.

Responses never include the email bodies. You only see your own application's notifications. When a notification fails for good, `notifyOnFailureTo` (optional) receives an alert.

## 4. Log users in from your backend

Users never talk to the Core directly. Your backend forwards their credentials with its M2M headers. Send `X-End-User-Ip` on every one of these calls.

**Login:** `POST /api/auth/login` with `{ "email", "password" }`:

```json
{
  "accessToken": "eyJ…",
  "tokenType": "Bearer",
  "expiresIn": 900,
  "refreshToken": "kq3…",
  "refreshTokenExpiresAtUtc": "2026-12-24T10:00:00Z"
}
```

- `accessToken` is a JWT for **your** application (`aud` = your `appKey`). It lives `expiresIn` seconds (15 minutes by default) and carries the user's `roles` and `permissions` in your application (section 6).
- `refreshToken` is opaque. Keep it **server-side**, or in an `HttpOnly`, `Secure` cookie. It lasts 30 days by default, and each refresh extends it.
- Errors:
  - `401 Auth.InvalidCredentials`: the same response whether the email is unknown or the password is wrong.
  - `403 Auth.UserLocked`, `Auth.EmailNotVerified` or `Auth.NoAccessToApplication`: returned only after a correct password.

**Refresh:** `POST /api/auth/refresh` with `{ "refreshToken" }` returns the same response.

- The refresh token **rotates**: store the new one and discard the old one.
- Presenting an old, already rotated token is treated as theft. It revokes the whole session (`401 Auth.RefreshTokenReused`), and the user must log in again.
- Refresh shortly before `expiresIn` runs out, and serialize refreshes per session, so two parallel requests do not both rotate the same token.
- The new access token carries the roles and permissions as they are now.

**Logout:** `POST /api/auth/logout` with `{ "refreshToken" }` always returns `204`.

- It revokes that session (refresh token family) only.
- An access token that was already issued stays valid until it expires, because there is no access-token blacklist. Keep access tokens short-lived and drop them on logout.

An admin can revoke a user's sessions with `DELETE /api/users/{id}/sessions`. Locking a user, or resetting their password, also revokes every session.

## 5. Account flows and the pages your app must host

The Core sends the account emails, signed with the Core sender. The links in them point to **pages of your application**: you pass their URLs, the Core appends `token=…`, and your page sends the token back through your backend.

The page URLs you pass (`verifyEmailUrl`, `resetPasswordUrl`) must meet these rules; otherwise you get `400 Auth.InvalidCallbackUrl`:

- absolute `https` URLs (`http` only for `localhost` / loopback);
- no user info and no `#fragment`;
- at most 2048 characters;
- no `token` query parameter of their own.

Passwords must be 10 to 128 characters (`400 Users.WeakPassword`).

| Flow | Your backend calls | Result |
|---|---|---|
| Sign up | `POST /api/auth/register` `{ email, password, fullName, verifyEmailUrl }` | `201 { userId }`. The user is created unverified, with access to your application and its default role. The `verify-email` email carries a link valid for 24 h. Requires `allowSelfRegistration` (otherwise `403 Auth.SelfRegistrationDisabled`). An email already registered in any application returns `409 Auth.EmailAlreadyRegistered`. |
| Verify email | `POST /api/auth/verify-email` `{ token }` | `204`. The email is verified and a `welcome-user` email is sent. A used, expired or foreign token returns `400 Auth.InvalidOneTimeToken`. |
| Resend verification | `POST /api/auth/resend-verification` `{ email, verifyEmailUrl }` | Always `202`. It sends only if the account exists, is active, is not verified and has access to your application, and at most once every 60 seconds. |
| Forgot password | `POST /api/auth/forgot-password` `{ email, resetPasswordUrl }` | Always `202`. It sends a `reset-password` link valid for 30 minutes, under the same conditions and at most once every 60 seconds. Older reset links stop working. |
| Reset password | `POST /api/auth/reset-password` `{ token, newPassword }` | `204`. The password changes and **every session of the user is revoked**. A weak password returns `400 Users.WeakPassword` and keeps the link usable. A bad token returns `400 Auth.InvalidOneTimeToken`. |

Pages your application hosts:

- **Verify email page** (`verifyEmailUrl`): reads `token` from the query string, sends it to your backend, which calls `verify-email`, and then shows "email verified" or "link expired, request a new one". The new-link button calls `resend-verification`.
- **Reset password page** (`resetPasswordUrl`): reads `token`, asks for the new password, and sends both to your backend, which calls `reset-password`.

`resend-verification` and `forgot-password` always answer `202`, so your UI must not say whether the email exists. Show a neutral message such as "if the account exists, we sent you an email".

## 6. Validate Core access tokens in a .NET satellite

Access tokens are RS256 JWTs:

| Field | Value |
|---|---|
| `iss` | The Core's public base URL (its `Jwt__Issuer`). |
| `aud` | Your `appKey`. |
| `typ` header | `at+jwt`. |
| Claims | `sub` (user id), `client_id` (your `appKey`), `email`, `name`, `jti`, `roles` and `permissions`. `roles` and `permissions` are JSON arrays of the user's role names and permission keys in your application. |

The Core publishes its keys at `/.well-known/jwks.json` and its metadata at `/.well-known/openid-configuration`, so the standard JWT bearer handler can discover them. The handler also follows key rotations, because previous public keys stay published during a rotation.

```csharp
using Microsoft.AspNetCore.Authentication.JwtBearer;

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = builder.Configuration["Core:Issuer"];
        options.Audience = builder.Configuration["Core:AppKey"];
        options.MapInboundClaims = false;
        options.TokenValidationParameters.ValidTypes = ["at+jwt"];
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.RoleClaimType = "roles";
    });

builder.Services
    .AddAuthorizationBuilder()
    .AddPolicy("orders.read", policy => policy.RequireClaim("permissions", "orders.read"));
```

- `Core:Issuer` is exactly the Core's `Jwt__Issuer`, for example `https://core-production.up.railway.app`.
- `Core:AppKey` is your `appKey`. A token issued for another application then fails validation.
- `Authority` must be `https`. For a Core running locally on `http://localhost`, also set `options.RequireHttpsMetadata = false`, and only in Development.
- `MapInboundClaims = false` keeps the claim names as issued (`sub`, `roles`, `permissions`).
- Authorize with permissions (`RequireClaim("permissions", …)`) or roles (`[Authorize(Roles = "admin")]`).
- A token carries the permissions the user had when it was issued. A change takes effect on the next refresh, at most `expiresIn` later.

## 7. Errors and rate limits

Every expected error is an RFC 7807 `ProblemDetails` (`application/problem+json`):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
  "title": "Conflict.",
  "status": 409,
  "detail": "A user with the specified email already exists.",
  "instance": "POST /api/users",
  "errorCode": "Users.EmailTaken",
  "correlationId": "5f0c…"
}
```

- **Branch on `errorCode`**: it is stable. `title` and `detail` are for humans and can change.
- A body or a number that cannot be parsed returns the framework's `400` `ValidationProblemDetails`, with an `errors` map and no `errorCode`.
- Unexpected failures return `500` without internal details. Report its `correlationId`.
- `/scalar` lists the `errorCode` values of each endpoint under its responses.

**Rate limits.** The auth endpoints are limited per application **and** end-user IP (`X-End-User-Ip`; an IPv6 address counts as its /64 network), in fixed one-minute windows:

| Limit | Endpoints |
|---|---|
| 10 per minute, shared | `login`, `register`, `forgot-password`, `resend-verification` |
| 60 per minute | `refresh` |
| 20 attempts per 15 minutes, per account | `login` (counted by email, across every application and IP; a correct password resets it) |

- Over any limit you get `429` with `errorCode` `RateLimit.Exceeded` and a `Retry-After` header in seconds (up to 15 minutes for the per-account limit). Wait that long before retrying, and show the user a "too many attempts" message.
- Requests without valid M2M credentials get `401` before the limiter.
- Other endpoints are not rate limited. Bodies have size limits: 8 MB for `POST /api/notifications/email` and the template administration, and 64 KB for the auth endpoints. A larger body returns `413` with `errorCode` `Request.TooLarge`.
