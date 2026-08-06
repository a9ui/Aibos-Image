# Security Policy

Aibos Image is a local-first native Windows application. Public source
visibility does not make it a public service.

- Do not commit personal media, generated caches, durable-state files, logs,
  credentials, API keys, cookies, private URLs, or machine-specific secrets.
- Use synthetic temporary fixtures when reporting or testing a problem.
- Preserve source images and durable state during normal viewing and testing.
- Keep explicit source deletion on the Windows Recycle Bin path; do not add a
  permanent-delete fallback.

Ordinary WPF viewing does not require a network service. The optional,
dedicated H25 Enhancement API companion is contacted only over loopback after
an explicit user action. It opens no Browser window and does not load the
Browser Viewer or its gallery data. It must remain bound to `127.0.0.1`; do not
expose it through a LAN listener, reverse proxy, tunnel, hosted deployment, or
the Internet.

The WPF and Browser applications will share a versioned durable-state contract.
Malformed or unsupported future state must fail non-destructively. Never reset,
truncate, replace, migrate, or merge a user's store merely to recover from a
read or compatibility failure.

## Reporting a vulnerability

Use **Security > Report a vulnerability** in this GitHub repository when the
private form is available. If it is unavailable, do not disclose sensitive
details publicly. Do not include credentials, private images, unredacted
absolute paths, cache/state files, or other personal data in a public issue or
pull request.

There are no published releases. Security fixes target the current active
development revision.
