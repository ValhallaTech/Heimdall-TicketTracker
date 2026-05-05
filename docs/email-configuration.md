# Email Configuration

Heimdall TicketTracker uses an `IEmailSender` seam (Phase 1 step 6 of [`docs/proposals/security-and-authorization.md`](./proposals/security-and-authorization.md)). Two implementations are registered at startup based on configuration:

| Implementation        | When it's selected                                                                                                | Behavior                                                          |
| --------------------- | ----------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------- |
| `MailKitEmailSender`  | `Email:Smtp` `Host`, `UserName`, `Password`, **and** `From` are all non-empty                                     | Sends real SMTP mail via MailKit + MimeKit                        |
| `NoOpEmailSender`     | Any of the four required keys above is empty                                                                      | Logs the suppressed delivery and returns successfully (no SMTP)   |

The chosen implementation is logged once at startup as `Email sender: {Implementation} ({Reason})`. The `Reason` only names which keys are present or missing — never their values.

## Configuration keys

The seam binds `SmtpOptions` to the `Email:Smtp` configuration section:

| Key                    | Type    | Default     | Description                                                                                                |
| ---------------------- | ------- | ----------- | ---------------------------------------------------------------------------------------------------------- |
| `Host`                 | string  | `""`        | SMTP server host name.                                                                                     |
| `Port`                 | int     | `587`       | SMTP server port. `587` = STARTTLS submission (the supported configuration).                               |
| `UserName`             | string  | `""`        | SMTP user name.                                                                                            |
| `Password`             | string  | `""`        | SMTP password. **Never put this in source control.** Only ever read inside `MailKitEmailSender`.           |
| `From`                 | string  | `""`        | Sender address used in the `From` header.                                                                  |
| `FromDisplayName`      | string? | `"Heimdall"`| Optional display name paired with `From`. Falls back to `"Heimdall"` when null.                            |
| `UseStartTls`          | bool    | `true`      | When `true`, connects with STARTTLS (port 587). When `false`, uses implicit TLS (port-465 style).          |
| `TimeoutMilliseconds`  | int     | `30000`     | SMTP client timeout.                                                                                       |

`appsettings.json` ships only empty placeholders — real values come from environment variables or user-secrets.

## Production: environment variables

ASP.NET Core's configuration provider maps double-underscore environment variables to nested config keys, so each `Email:Smtp` value has a one-to-one env var:

```bash
Email__Smtp__Host=smtp.example.com
Email__Smtp__Port=587
Email__Smtp__UserName=postmaster@your-domain
Email__Smtp__Password=...redacted...
Email__Smtp__From=heimdall-support@your-domain
Email__Smtp__FromDisplayName=Heimdall
Email__Smtp__UseStartTls=true
Email__Smtp__TimeoutMilliseconds=30000
```

On Render, set these via the dashboard's **Environment** secret store. They never appear in source.

## Local development: user-secrets

For local-dev runs, prefer `dotnet user-secrets` over editing `appsettings.Development.json` so credentials never reach version control:

```bash
cd src/Heimdall.Web
dotnet user-secrets set "Email:Smtp:Host" "smtp.example.com"
dotnet user-secrets set "Email:Smtp:Port" "587"
dotnet user-secrets set "Email:Smtp:UserName" "postmaster@your-domain"
dotnet user-secrets set "Email:Smtp:Password" "...redacted..."
dotnet user-secrets set "Email:Smtp:From" "heimdall-support@your-domain"
```

If you skip these steps the app still boots — `NoOpEmailSender` is wired up automatically and outgoing email is logged but suppressed. That is the supported workflow for any environment where SMTP has not been provisioned.

## Provider notes

The supported transactional-email provider is **Mailgun**. The configuration above (port `587`, `UseStartTls=true`) matches Mailgun's submission endpoint. Other RFC-compliant SMTP providers should also work — they go through the same MailKit code path.
