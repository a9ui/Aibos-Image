# Security policy

Aibos Image is a local-first Windows application, not a public service.

## Required boundaries

- Do not commit personal media, credentials, private URLs, machine-specific
  paths, durable-state files, databases, caches, logs, or generated runtime
  output. Use synthetic TEMP fixtures.
- Normal viewing and tests preserve source images and durable state. Explicit
  source deletion uses the Windows Recycle Bin and has no permanent-delete
  fallback.
- The optional Enhancement companion stays on `127.0.0.1`. Do not expose it
  through a LAN listener, proxy, tunnel, hosted deployment, or the Internet.
- Loopback is not identity. Authenticate the companion before sending sensitive
  data, authenticate protected requests and responses, prevent replay, and fail
  closed if ownership cannot be proved.
- Treat paths, metadata, process data, API data, and durable files as untrusted.
  Bound inputs and reject malformed or unsupported future state without
  rewriting it.
- Never reset, truncate, replace, merge, or migrate user state merely to recover
  from a read or compatibility failure.

Protocol details belong in the affected machine-readable contract, especially
`contracts/enhancement-companion-auth-v2.json`, rather than being duplicated
here.

Public branches and review material must follow
[`docs/publication-boundary.md`](docs/publication-boundary.md). A clean working
tree does not prove that branch history is safe to publish.

## Reporting a vulnerability

Use **Security > Report a vulnerability** in this GitHub repository when the
private form is available. Otherwise, do not disclose sensitive details in a
public issue.

Do not include credentials, personal images, private paths, state or database
files, logs, or exploit details that expose users. There are no published
releases; fixes target the current development revision.
