# Documentation index

This page routes a change to its authority, implementation map, and evidence.
It is not a second product contract.

## Read path

1. Read [`AGENTS.md`](../AGENTS.md) and inspect the current diff.
2. Use the table below to select the narrowest authority for the change.
3. Use the architecture maps to find the current owner and focused verifier.
4. Treat review packets, inventories, and historical material as evidence only.

| Change surface | Read next |
|---|---|
| Public setup or launch entry points | [`README.md`](../README.md) |
| Security, privacy, or publication boundary | [`SECURITY.md`](../SECURITY.md) and [`publication-boundary.md`](publication-boundary.md) |
| Stable product behavior | The matching section of [`product-contract.md`](product-contract.md) |
| Durable state or Enhancement protocol | The matching entry in [`contracts/index.json`](../contracts/index.json), then only its named contract or fixture |
| Code or responsibility ownership | [`architecture/project-map.md`](architecture/project-map.md) |
| Cross-feature state | [`architecture/state-ownership.md`](architecture/state-ownership.md) |
| Startup, passive reads, enqueue, Jobs, or output flow | [`architecture/critical-flows.md`](architecture/critical-flows.md) |
| Video Tools design context | [`video-studio-design.md`](video-studio-design.md), after the applicable contract |

## Classification

| Class | Documents | Meaning |
|---|---|---|
| Product and protocol authority | [`product-contract.md`](product-contract.md), [`contracts/index.json`](../contracts/index.json), and the selected versioned contract | Stable meaning and exact protocol shape. A conflict must be resolved in the same patch. |
| Repository and trust authority | [`AGENTS.md`](../AGENTS.md), [`README.md`](../README.md), [`SECURITY.md`](../SECURITY.md), [`publication-boundary.md`](publication-boundary.md) | Working, launch, security, and publication rules. These do not redefine product protocol fields. |
| Maintainer reference | Files under [`architecture/`](architecture/) and [`video-studio-design.md`](video-studio-design.md) | Navigation and design context. They point to authority rather than copying it. |
| Review evidence | [`review-packets/`](review-packets/) | Revision-bound review material. It does not override current code or contracts. |
| Generated historical inventory | [`legacy-ledger/`](legacy-ledger/) and [`legacy-disposition/`](legacy-disposition/) | Generated inventories for retired assets and their disposition. They are not active product scope. |

## Maintenance rule

- When responsibility moves, update the relevant architecture row in the same
  patch and keep links to exact code, contract, and test seams.
- Do not copy wire fields, bounds, or stable semantics out of their versioned
  contract. Link to the authority instead.
- Runtime observations, local paths, user data, prompts, screenshots, queue
  contents, and private implementation do not belong in this repository.
- A documentation-only Atlas change must not alter source, UI, protocol, or
  durable-state behavior.
