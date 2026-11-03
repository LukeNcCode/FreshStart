# Security Policy

## Supported versions

Security fixes are applied to the latest release line. Older builds may not
receive patches.

## Reporting a vulnerability

Please use GitHub Security Advisories or the repository's private vulnerability
reporting feature. Do **not** open a public issue for:

- privilege escalation;
- command or argument injection;
- path traversal;
- unsafe registry or file deletion;
- secret exposure;
- bypass of confirmation, backup, or rollback gates;
- supply-chain or package-verification weaknesses.

Include the affected version, Windows build, reproduction steps, expected and
actual behavior, and a minimal proof of concept. Redact usernames, paths,
tokens, registry identifiers, and other personal data from logs.

## Security model

FreshStart requires administrator rights for many operations. It is designed to:

- modify only explicitly supported registry locations;
- snapshot state before writing where rollback is supported;
- validate downloads with HTTPS, size limits, hashes, and Authenticode where available;
- avoid arbitrary user-provided scripts in the execution center;
- keep configuration, backups, and logs local.

The application does not protect against a compromised administrator account or
malicious local software already running with the same privileges.
