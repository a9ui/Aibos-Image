# Storage layout and maintenance

This map describes ownership and placement, not another path configuration.
Stable rules are in the [storage boundary](../product-contract.md#storage-placement-and-maintenance)
and the relevant entries in the [contract index](../../contracts/index.json).
Resolve the actual paths on the installation being maintained. Do not copy a
developer's directory names into product defaults.

## Placement by responsibility

| Area | Placement authority and owner | Retention and maintenance |
|---|---|---|
| WPF executable and launch registration | Explicit repository path selected by the desktop installer; the launcher checks the Release target against its source and dependency provenance. | Keep the registered executable, required dependencies, and source/build inputs. The current desktop launcher is a source-checkout deployment, not a versioned standalone package. |
| Optional Companion executable | The explicitly selected external Companion root and trusted Node installation. WPF does not discover it by scanning sibling repositories. | Its deployment owns runtime dependencies and build output. Retirement of a web frontend does not imply that the API runtime or its packages can be removed. |
| Shared durable state | The process-lifetime root resolved by `SharedDataRootLocator`, with the exact missing/invalid/future behavior specified by `PV-ROOT-001`. | Protect the resolved root, even if it is inside an old checkout or named `.cache`. All active readers must agree on its identity. |
| Managed generated media | The output-root protocol, owned by Companion publication. | Treat completed media and their dependency relationships as user data. They are not build artifacts. |
| WPF presentation state and Styles | Existing `%LocalAppData%/PhotoViewer.Wpf` compatibility storage; local persistence owns the fixed files. | Keep preferences, Styles, and unknown compatible fields. Renaming the product does not migrate this directory. |
| Companion authentication | `EnhancementCompanionAuthStoragePath` selects fixed application-owned storage below LocalApplicationData. | Never copy authentication content into inventories, logs, review packets, or cleanup manifests. |
| Metadata index | `MetadataIndexStorePath` selects an application-owned catalog cache below LocalApplicationData. | Cache ownership and a supported rebuild path must be established before a separate cache maintenance operation. General storage cleanup does not remove it. |
| Model files, sealed runtimes, and job staging | The selected runtime's deployment and operation contracts; accepted jobs own their frozen model/LoRA selections and owned staging. | Keep referenced versions, current mounts, pending/recoverable requests, and designated rollback material. A version filename alone is not a retention decision. |
| Synthetic build/test artifacts | The particular verifier's explicitly created TEMP directory. | The verifier owns cleanup only after its process has ended and the needed acceptance evidence is retained. Do not scan and delete arbitrary TEMP contents. |

Implementation entry points:

- [Desktop installation](../../scripts/install-aibos-desktop-launcher.ps1),
  [launch preparation](../../scripts/start-aibos-desktop.ps1), and
  [build provenance](../../scripts/check-wpf-launch-target.ps1).
- [Shared-root resolver](../../local-native/PhotoViewer.Wpf/SharedDataRootLocator.cs)
  and [root contract](../../contracts/shared-root-locator-v1.json).
- [Local persistence](../../local-native/PhotoViewer.Wpf/LocalPersistenceStorePath.cs),
  [authentication storage](../../local-native/PhotoViewer.Wpf/EnhancementCompanionAuthStoragePath.cs),
  and [metadata index storage](../../local-native/PhotoViewer.Wpf/MetadataIndexStorePath.cs).

## Bounded maintenance

Identify a concrete target and its owner before measuring it. Limit traversal to
the named directory, impose a time/file bound, report incomplete reads, and do
not follow redirected descendants. An incomplete inventory is not deletion
evidence. Logical file length is useful for comparison, but hard-link identity
and allocated storage determine whether removing a particular copy saves space.

For an obsolete generated target, establish its exact identity, current
references, reproducible source, and recovery requirements. Recheck those facts
at the mutation boundary. Preserve files that differ from the proven generated
copy. Do not replace an existing target during rollback or merge two directory
trees after a partial failure.

When a maintenance operation needs the application or a worker stopped, use the
existing authenticated ownership and normal shutdown path. A read-only scan does
not stop or resume Jobs. Restore normal operation only after the caller confirms
that every task requiring the pause has finished.

Record a same-volume rename as a reversible isolation step, not a space saving.
Count reclaimed bytes only after deletion and a fresh free-space measurement;
concurrent writes can change that measurement. Keep machine-specific inventory
and maintenance evidence outside this public repository.

## Moving to a different deployment or data root

Treat executable deployment and durable-data migration as separate changes.
Each needs an explicit destination and rollback boundary. For a data migration,
prove preservation of the relevant logical data and unknown fields across every
active reader; copying files or obtaining a successful launch is insufficient.
Preserve queue identity, order, immutable instructions, output references, and
authentication boundaries. Do not create a second independent path authority in
an inventory or maintenance record.
