# Public branch and review boundary

Aibos Image is public source, but the machine and media library used to develop
it are private. Every branch, pull request, patch, log excerpt, fixture, and
review packet must preserve that boundary.

## Never publish

- Personal or source media, generated output, thumbnails, model files, runtime
  bundles, caches, queue contents, or durable application state.
- User-written prompts, prompt history, generation metadata, command history,
  or copied production responses.
- Credentials, cookies, tokens, private URLs, local service secrets, or private
  security reports.
- Account identifiers, machine-specific names, unredacted absolute paths, or
  filenames that reveal private media or user activity.
- A local or private branch history merely because its current working tree is
  clean. Private ancestors remain part of a pushed Git graph.

Synthetic fixtures may be published only when they contain no transformed or
recoverable user data. Use neutral placeholders such as `<user>`,
`<local-root>`, `<prompt-redacted>`, and `PRIVATE-A` in documentation and test
evidence.

## Moving a safe change into a public candidate

1. Start the candidate from the public `main` branch or another reviewed public
   commit.
2. Reapply the required source diff, or cherry-pick only an individually
   reviewed commit. Review its patch, message, filenames, and metadata first.
3. Do not merge or push the private lineage. A source diff that is safe does
   not make its private ancestors safe.
4. Inspect the complete candidate diff and all candidate-only commits before
   push. Keep the public candidate free of local evidence artifacts.
5. Build and test from the candidate, then prepare the review packet from that
   exact revision.

## When a reviewer needs history

Provide a redacted change map instead of raw private history. The map may name
public commit IDs and opaque private labels such as `PRIVATE-A`, followed by a
short functional summary. It must not include raw `git log` output, bundles,
archives, patches, prompts, paths, account names, or other private payloads.

If the reason for a change cannot be explained without private data, describe
the observable product behavior and the synthetic reproduction instead. Do not
publish the original data.
