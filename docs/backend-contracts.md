# Backend contracts — Workout Sessions (Phase 1: history sync & deletion)

Backend is the `.NET` API in this repo (ASP.NET Core / EF Core / PostgreSQL), deployed
on Railway. There is no PocketBase involved anywhere in this stack — if any older
documentation elsewhere describes a PocketBase backend, it's stale; this repo has never
used PocketBase. (Searched this repo for any such reference before writing this file —
none exist here.)

This document covers the `WorkoutSession` API surface as of Phase 1 (durable
cross-device history sync + soft-delete). It does not cover the rest of the API
(auth, templates, friends, macro-profile, etc.) — those are unchanged by this phase.

---

## 0. Conventions (unchanged, restated for context)

- `Authorization: Bearer <jwt>` on every endpoint below.
- Non-2xx responses return `{ "message": "..." }`.
- JSON fields that are `null` are omitted from the response entirely (server-side
  `JsonIgnoreCondition.WhenWritingNull`) — a client should treat "field absent" and
  "field null" identically.
- All timestamps are UTC ISO-8601 on the wire.

## 1. `WorkoutSession` model

| Field | Meaning | Notes |
|---|---|---|
| `Id` | Client-generated GUID | The entire idempotency mechanism. Global primary key — never reused across users. |
| `UserId` | Owner | |
| `TemplateId`/`Name`/`Icon` | Soft reference to the template used | No FK — deleting a template never touches session history. |
| `StartedAt` / `EndedAt` | When the workout actually happened | Business meaning. Drives the recent-history list and streak calculation. |
| `DurationMs` | | |
| `LogsJson` | Full exercise/set/planned-set data, `jsonb` | Stored verbatim, whatever shape the client sends. This was already durable before Phase 1 — the recoverability gap was entirely about *fetching* it, not storing it. |
| `CreatedAtServer` | When the server first received the row | Immutable once set. |
| **`UpdatedAt`** *(new)* | When this row last changed, from a sync-bookkeeping point of view | Bumped on insert **and** on soft-delete. This is what cursor pagination and tombstone discovery key off — **not** `EndedAt` and **not** `CreatedAtServer`. |
| **`DeletedAt`** *(new)* | Soft-delete tombstone | `null` = active. The row is never physically removed. |

**Why three different timestamps.** These are genuinely different axes and the code
deliberately keeps them separate:
- `EndedAt` — when the workout happened (what a "last 7 days" or streak query cares about).
- `CreatedAtServer` — when the server first saw the row (audit/debugging value only).
- `UpdatedAt` — when the row last changed from a *reconciliation* point of view (what
  cursor pagination cares about). A delete bumps this exactly like an insert does, so a
  tombstone is discoverable by any device polling `since=<last cursor>`, even if that
  device already paged past the row's original `CreatedAtServer`/`EndedAt` long ago.

## 2. `POST /api/workout-sessions/sync` (unchanged contract)

Batch upsert-by-client-id. Request/response shape is **exactly what it was before
Phase 1** — no client changes required to keep working.

```json
// request
{ "sessions": [ { "id": "<guid>", "templateId": "...", "templateName": "...", "templateIcon": "...", "startedAt": "...", "endedAt": "...", "durationMs": 0, "logs": [ /* arbitrary */ ] } ] }
// response
{ "savedIds": ["<guid>", ...], "serverTime": "..." }
```

**Semantics (Phase 1 clarifications, behavior is additive):**
- **Idempotent across requests**: replaying a batch is a no-op for ids already known
  server-side (including soft-deleted ones — see below).
- **Idempotent within a single request** *(fixed in Phase 1)*: if the same `id`
  appears more than once in one `sessions` array, the **first occurrence wins** —
  every occurrence is acknowledged in `savedIds`, but only the first is written.
  Previously, a duplicate id within one batch would throw a database unique-key
  exception and fail the entire request with a 500; this is now a defined, tested,
  harmless no-op for the duplicate.
- **A soft-deleted id is never resurrected by sync.** If a client (any device, even
  the one that deleted it) syncs an id that already exists server-side as a
  tombstone, it's treated exactly like any other already-known id: acknowledged in
  `savedIds`, left untouched. `DeletedAt` is never cleared by `Sync`.
- **Maximum batch size: 500 sessions per request.** Exceeding this returns
  `400 { "message": "A sync batch can contain at most 500 sessions." }` and writes
  nothing. Normal offline-then-reconnect usage (a device's queued new workouts) is
  far below this; the cap exists purely to reject a malformed or runaway request.
- **Authoritative fields**: on first insert, every field comes from the client. Once
  an id exists server-side, `Sync` never updates it (sessions are immutable/append-only
  by design) — the only way an existing session's server-side state changes at all is
  via `DELETE`.

## 3. `GET /api/workout-sessions?sinceDays=N` (unchanged contract, corrected behavior)

**Unchanged**: same query params (`sinceDays`, `since`), same response shape (a bare
array of session DTOs), still the fast "recent workouts" path — this is what
`bootstrap` uses, and it stays cheap and unpaginated because the window is small.

**Corrected**: now excludes soft-deleted sessions (`DeletedAt == null`). Before Phase
1 there was no such concept; this is a required correctness fix, not a contract
break — a session deleted on one device must disappear from another device's recent
list, which is exactly what this restores.

Also excluded from: `/api/sync/bootstrap`, both friends-ranking endpoints
(`/api/workout-sessions/{templateId}/friends-ranking` and
`/api/exercises/{exerciseId}/friends-ranking`), and the streak calculation.

## 4. `DELETE /api/workout-sessions/{id}` *(new)*

```
DELETE /api/workout-sessions/{id}
Authorization: Bearer <jwt>
```

- `204 No Content` in all cases: it existed and got deleted, it was already deleted,
  it doesn't exist, or it belongs to someone else — the response never reveals which
  (same idempotent-delete convention used by `DELETE /api/templates/{id}`).
- Only the owning user's session is ever affected — verified by `UserId` match, not
  just presented in the URL.
- Soft-delete only: sets `DeletedAt = UtcNow`, `UpdatedAt = DeletedAt`. The row is
  never physically removed.
- Triggers the same `RecomputeStreakAsync` that `Sync` already calls — the deleted
  session is immediately excluded from streak calculation. See §7.

## 5. `GET /api/workout-sessions/history` *(new — durable reconciliation endpoint)*

The endpoint a device uses to get (or catch up on) the user's *complete* history,
independent of the 7-day window. Same mechanism serves both:

- **Initial backfill** (new device, empty local cache): start with `cursor` omitted,
  keep requesting pages until `hasMore` is `false`.
- **Incremental sync** (device resuming later): pass the `nextCursor` it saved from
  its last successful page as `cursor`. Only rows that changed (inserted *or*
  soft-deleted) since then come back.

```
GET /api/workout-sessions/history?cursor=<opaque>&limit=200&includeDeleted=true
```

| Param | Default | Notes |
|---|---|---|
| `cursor` | none (start from the beginning) | **Opaque** — the client only ever round-trips a value it received in `nextCursor`. Never construct or parse it. Invalid/garbage cursors are treated as "start from the beginning," not an error. |
| `limit` | 200 | Clamped to `[1, 500]` server-side — an out-of-range value is silently clamped, not rejected. |
| `includeDeleted` | `true` | Tombstones are the whole point of this endpoint for reconciliation purposes; set `false` only if you specifically want active sessions only. |

Response:

```json
{
  "sessions": [ { "...": "same shape as every other session DTO, plus deletedAt" } ],
  "nextCursor": "opaque-string-or-omitted-if-no-more-pages",
  "hasMore": true
}
```

Ordering is `(UpdatedAt, Id)` ascending — stable and monotonic, so pages never skip or
duplicate rows even if new syncs/deletes happen concurrently with a paged backfill.

## 6. Tombstones

`WorkoutSessionDto` gained one field: `deletedAt` (nullable ISO-8601 string). For every
active session this is `null` and therefore **omitted from the JSON entirely** — a
client that doesn't know this field exists never sees anything change shape for
sessions it already handles today. It only appears, populated, on tombstones returned
by `/history?includeDeleted=true`.

**Confirmed behavior (tested live against the real database, not just unit tests):**
1. Device A syncs session X. Device B (same account) syncs the same X — no-op, already
   known.
2. Device A deletes X. Tombstone created, `UpdatedAt` bumped.
3. Device B calls `/history?includeDeleted=true` — X comes back with `deletedAt` set.
   Device B removes its local copy.
4. X is absent from Device B's `?sinceDays=N` list and from `bootstrap` immediately
   after the delete — no need to wait for a `/history` reconciliation pass for the
   *recent* view specifically.
5. If Device B still has X queued locally and syncs it anyway (hadn't processed the
   tombstone yet), the sync is a no-op: X's `DeletedAt` is untouched, never resurrected.

## 7. Streak interaction

The streak rule itself is unchanged (see the existing `StreakCalculator` — same
calendar-day-gap logic, untouched by this phase). What changed:

- `RecomputeStreakAsync`'s source query now excludes soft-deleted sessions.
- `DELETE /api/workout-sessions/{id}` calls the exact same `RecomputeStreakAsync` that
  `Sync` already called — no second streak implementation, no special-casing for
  "what if the deleted session was the most recent one." Because the recompute always
  runs fully from scratch (not incrementally) over whatever's currently active, deleting
  any session — including the one anchoring the current streak — just naturally
  produces the correct new answer on the next recompute.
- `BestStreak` is a monotonic high-water mark (`Math.Max`) and is **never** reduced by
  a deletion, no matter how much `CurrentStreak` drops (including all the way to 0).

## 8. Backward compatibility

Nothing existing changed shape. Old clients that have never heard of `DELETE` or
`/history`:
- Keep working against `sync` and `?sinceDays=N` exactly as before.
- Never see `deletedAt` populated (it's always absent for anything they'd normally see).
- Are unaffected by the 500-sessions-per-batch cap unless they were already sending
  batches that large (extremely unlikely for normal offline-queue usage).

## 9. What Phase 1 does *not* cover

- Custom exercise synchronization — explicitly deferred to a later phase pending a
  decision on cross-device-safe custom exercise IDs (the current client-side scheme,
  `>= 1,000,000` local auto-increment, has no collision resistance across devices).
- Session **editing** — sessions remain immutable/append-only once synced; only
  deletion (via tombstone) changes a session's state after creation.
- A genuinely astronomically unlikely edge case: `WorkoutSession.Id` is a *global*
  primary key (by original design, predating this phase) — a GUID collision between
  two different users' independently-generated ids would 500 rather than fail
  gracefully. Not touched in this phase (out of scope, and the probability is
  negligible for v4 GUIDs), but noted for the record.
