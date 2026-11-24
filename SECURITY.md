# Security policy

Href.Core is the identity and access API of the Href platform: users, applications, roles, permissions, JWT issuance and account emails. We take reports about it seriously.

## Supported versions

Only the latest release (the `main` branch, deployed to production) receives security fixes. The project is pre-1.0, so a fix ships in a new release and is not backported.

| Version         | Supported |
| --------------- | --------- |
| Latest release  | Yes       |
| Older releases  | No        |

## Reporting a vulnerability

Do not open a public issue, pull request or discussion for a vulnerability.

Report it privately through GitHub: open the [Security tab](https://github.com/hrefsolutions/Core/security) and select **Report a vulnerability**, or go directly to the [new advisory form](https://github.com/hrefsolutions/Core/security/advisories/new). Only the maintainers can see the report.

Include as much of this as you can:

- the affected endpoint, component or file, and the version or commit;
- the steps to reproduce it (requests, payloads, configuration);
- the impact you expect (for example account takeover, data exposure, privilege escalation);
- any fix or mitigation you suggest.

Do not include real user data, credentials or tokens in the report. Use test accounts.

## What happens next

1. We confirm that we received the report within 5 business days.
2. We investigate and tell you whether we accept the report, with a severity, within 15 business days.
3. We prepare the fix in a private fork of the advisory, release it, and publish the advisory with a CVE when it applies.
4. We credit you in the advisory, unless you ask us not to.

Please give us a reasonable time to release the fix before you disclose the vulnerability publicly.

## Scope

In scope: the code in this repository and the API that it deploys.

Out of scope:

- denial of service by request volume, and reports from automated scanners without a proof of impact;
- vulnerabilities in third-party dependencies that already have a public advisory (Dependabot tracks them), unless this project uses the dependency in a way that makes it exploitable;
- social engineering and physical attacks.
