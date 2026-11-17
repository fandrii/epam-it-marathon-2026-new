# Guest-session capacity and creation retries

## API contract

The existing `POST /guest/sessions` accepts an optional `idempotencyKey` in its JSON body:

```json
{
  "groupReservationId": "group-123",
  "guestName": "Alex",
  "idempotencyKey": "568d1e23-7882-49fa-99db-25816cbdfdc0"
}
```

The Angular client generates a UUID per new creation attempt, persists the pending
request before sending it, and reuses it on retries and reload recovery. A deliberate
new pre-order gets a new key, even if the guest name is unchanged. Names are never
used as participant identities.

Same key and identical group/name returns the original `SessionId` and `SessionToken`.
Same key with different request data returns HTTP 409. Replaying a deleted session
also returns 409; it does not resurrect it. Request keys are global to creation and
are hashed before storage. Treat them as sensitive because replay returns the
original creation response. Cart access continues to require the guest token;
Owner cart access continues to use Cognito.

Older callers may omit the key. Each such call remains a distinct creation attempt,
but still uses atomic capacity enforcement. Network retries from those older callers
cannot be deduplicated without a stable key.

Invitation details remain readable at full capacity so returning guests can reopen
their invitation. Creating an additional participant is rejected at capacity.

## Additive persistence changes

No table, key, GSI, IAM, Cognito, Terraform, or Syndicate resource changes are required.

- Existing `GroupReservations` items acquire numeric `participantCount` and
  `participantRevision` attributes on the first successful membership mutation.
  The count includes exactly one Owner and all persisted non-owner sessions,
  including submitted sessions. The revision provides compare-and-swap protection.
- `GuestSessions` also stores creation receipts under the reserved primary-key
  prefix `creation-`, followed by the hashed request key. A receipt contains
  `fingerprint`, `responseSessionId`, and `responseToken`. It deliberately omits both
  `groupReservationId` and `guestSessionTokenHash`, so it cannot appear in participant
  listings, capacity scans, or guest-token queries. These records contain sensitive
  response data and must receive the table's existing access/encryption protection.
  They are retained with no TTL so a retry never silently becomes a new participant.

The group counter, conditional reservation validation, session insert, and receipt
insert commit in one DynamoDB transaction. Deletion removes the session and adjusts
the counter in one transaction. Owner initialization is a conditional insert and
cannot overwrite an existing Owner cart. Cart saves require an existing session,
preventing a racing save from recreating a deleted participant.

## Legacy groups and rollout

No eager data migration is needed. When a group has no counter, a strongly consistent,
paginated base-table scan obtains its current non-owner sessions. The first write
initializes the count conditionally; competing writers retry against the committed
counter. Read-only capacity/status requests do not initialize or modify records.
The initial scan costs scale with the GuestSessions table size; later reads use the
group counter. Existing Owner flags stored as BOOL or numeric 1 are supported.

All membership writers must be upgraded together. Do not send guest creation or
deletion traffic to old Lambda versions while this protocol is active: old writers
do not maintain the counter. For rollback, first stop membership writes, then use an
approved reconciliation procedure before returning to this protocol. The new code
must not be deployed with a weighted alias mixing old/new membership writers.

The transaction uses the existing GetItem, Scan, PutItem, UpdateItem, and DeleteItem
permissions. Conditional no-op updates validate the reservation and serialize with
edits without introducing ConditionCheckItem permissions. No IAM changes were made.

Local tests evaluate actual transaction requests with a conditional, atomic in-memory
DynamoDB test double. A non-production DynamoDB integration/smoke test is still
recommended after deployment approval; no live records were accessed or modified.

## Changed files

Paths below are relative to the repository root.

Backend implementation:

- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Services/GuestSessions/GuestSessionService.cs`
- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Services/GroupReservations/GroupReservationService.cs`
- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Repositories/GuestSessions/GuestSessionRepository.cs`
- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Repositories/GuestSessions/GuestSessionRepository.Creation.cs` (new)
- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Repositories/GuestSessions/IGuestSessionRepository.cs`
- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Models/GuestSessions/CreateSessionRequest.cs`
- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Handlers/GuestSessions/CreateGuestSessionHandler.cs`
- `backend/restaurant-backend-app/dnapp/lambdas/api-handler/Exceptions/GuestSessionCreationConflictException.cs` (new)

Backend tests:

- `backend/restaurant-backend-app/Tests/Services/GuestSessionServiceTests.cs`
- `backend/restaurant-backend-app/Tests/Services/GroupReservationServiceTests.cs`
- `backend/restaurant-backend-app/Tests/Services/GetGroupSessionsTests.cs`
- `backend/restaurant-backend-app/Tests/Repositories/GuestSessionCreationTests.cs` (new)
- `backend/restaurant-backend-app/Tests/Handlers/CreateGuestSessionHandlerTests.cs` (new)

Client and client tests:

- `frontend/restaurant-frontend-app/src/app/services/guest-sessions/guest-sessions.service.ts`
- `frontend/restaurant-frontend-app/src/app/services/guest-sessions/guest-sessions.service.spec.ts`
- `frontend/restaurant-frontend-app/src/app/pages/guest-pre-order-page/state/guest-preorder.store.ts`
- `frontend/restaurant-frontend-app/src/app/pages/guest-pre-order-page/state/guest-preorder.store.spec.ts` (new)
- `frontend/restaurant-frontend-app/src/app/pages/guest-pre-order-page/ui/guest-name-modal/guest-name-modal.component.ts`
- `frontend/restaurant-frontend-app/src/app/pages/guest-pre-order-page/ui/guest-name-modal/guest-name-modal.component.html`
- `frontend/restaurant-frontend-app/src/app/pages/guest-pre-order-page/guest-pre-order-page.component.html`

This document is new. Temporary local test-launcher files are removed after verification.
