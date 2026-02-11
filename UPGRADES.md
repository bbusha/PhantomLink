# UnityExternalModdingTool Upgrades

## Detection System Improvements

- The discovery engine now uses adaptive signals in addition to static name/type/context heuristics:
  - Per-game token learning from confirmed mappings and user feedback.
  - Per-game causal priors from event-window tests (damage/ammo/sprint/buy/cooldown).
  - Entity-affinity scoring to reduce UI/system false positives (player vs weapon vs UI).

## Dynamic Cheats

- The GUI includes a Dynamic Cheats tab that generates game-specific cheat recommendations using:
  - Current scan results (candidate list + writability).
  - Known behavior signatures and inferred current→max relationships when available.
  - Knowledge-base priors (confirmed mappings, learned tokens, causal priors).
- Recommendations can be applied directly and recorded to the per-game knowledge base.
- Users can upvote/downvote recommendations; feedback is stored and feeds the token learner.

## Cheat Definition Versioning

- Cheat definitions stored in the knowledge base are versioned with:
  - A monotonically increasing revision number.
  - A SHA-256 signature of the current cheat set.
  - A bounded history of previous cheat-set snapshots for rollback/compare workflows.

## Performance Monitoring

- The Dynamic Cheats tab displays basic timing metrics for:
  - Scan duration
  - Recommendation refresh duration
  - Apply duration
  - Active-cheat poll duration

## Upgrade / Backward Compatibility

- All new fields are additive to the JSON knowledge base schema and default safely when missing.
- Older knowledge base files remain loadable; new fields are populated as the tool learns.

