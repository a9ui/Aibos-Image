# Security Policy

Aibos Image is a local-first application. Public source visibility does not
make the application a public service.

- Bind the Browser runtime only to `127.0.0.1`.
- Do not expose the runtime through a LAN listener, reverse proxy, tunnel, or
  hosted deployment.
- Do not commit personal media, generated caches, state databases, logs,
  credentials, API keys, cookies, private URLs, or machine-specific secrets.
- Use synthetic temporary fixtures when reporting or testing a problem.

Aibos Image APIs can read or act on explicitly selected local files and are not
designed as an authenticated Internet API.

## Reporting a vulnerability

Use **Security > Report a vulnerability** in this GitHub repository when the
private form is available. If it is unavailable, do not disclose sensitive
details publicly. Do not include credentials, private images, unredacted
absolute paths, or other personal data in a public issue or pull request.

There are no published releases. Security fixes target the current active
development revision.
