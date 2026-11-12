# Security

Please use [GitHub private vulnerability reporting](https://github.com/MaxJafar/obelisk/security/advisories/new) for security issues. Do not post credentials, private hostnames, recordings or live telemetry in public issues.

This is an early Windows source release. Relevant boundaries include Gateway pairing, DPAPI-protected secrets, authenticated loopback inference, verified SSH hosts, explicit provider opt-in and container control allowlists. Only the latest main branch receives fixes at present; there is no formal response-time commitment.

If you accidentally expose a credential, revoke it at its issuer, remove it from source and history, and check any published artifacts. Deleting the visible file is not a substitute for revocation. This repository's checks are preventive tooling, not a security certification.
