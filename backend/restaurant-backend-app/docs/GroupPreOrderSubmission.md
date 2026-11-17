# Submit a group pre-order into an existing order

`POST /group-reservations/{groupReservationId}/submit` uses the existing Cognito
authorizer. The caller's `sub` must own the reservation identified in the body.

```http
POST /group-reservations/group-123/submit
Authorization: Bearer <Cognito token>
Content-Type: application/json

{"reservationId":"reservation-123"}
```

The group must belong to that reservation. Existing reservation status, pre-order
cutoff and open-group validation apply. Only GuestSessions marked `IsSubmitted`
contribute dishes; other sessions, including an unsubmitted Owner session, are ignored.

For submitted sessions containing `dish1` quantities 1 and 2, plus `dish2` quantity 2,
the HTTP 200 body is exactly:

```json
[
  { "dishId": "dish1", "quantity": 3 },
  { "dishId": "dish2", "quantity": 2 }
]
```

This array reports the quantities contributed by submitted sessions. It does not
include pre-existing order quantities. For example, if the existing order already
contains `dish1` quantity 4, the stored order ends with `dish1` quantity 7 and `dish2`
quantity 2. Its ID, unrelated items and other order metadata are preserved.

## Errors and persistence

- 401: no authenticated Cognito identity.
- 403: caller does not own the reservation.
- 404: reservation or group reservation does not exist.
- 400: invalid input, unrelated group/reservation, invalid reservation/group state,
  no submitted sessions, no submitted dishes, missing active order, invalid quantities,
  incompatible existing item states, repeated confirmation or a stale write conflict.
- Unexpected persistence failures use the existing internal-error handling.

The endpoint never creates a missing Order. Order creation must occur separately
through the application's existing functionality before this API is called.

Submitted quantities are aggregated by exact `dishId` and added to existing order
quantities. Existing item states are retained; conflicting states on duplicate
existing rows are rejected instead of silently discarded. Additions overlapping an
existing Confirmed or Canceled row are also rejected, preventing new quantities
from inheriting a waiter decision. New rows are Pending. The existing order must
be in Created status.

A conditional DynamoDB transaction updates the existing order to
`SubmittedByCustomer` and the group to `Submitted`. An exact snapshot comparison
protects the original order dishes/status from stale overwrites; the group must
still be open and related to the reservation. The transaction also verifies that
the reservation remains reserved and owned by the same user. A failed condition
commits none of these writes. A stale request must reload and retry; it does not
silently reapply quantities against an outdated order.

No new table, index, version attribute or eager record migration is required.
Session selection uses a strongly consistent, paginated base-table scan so a stale
submission flag in the session GSI cannot silently exclude an already-submitted
participant. This terminal read costs scale with the GuestSessions table size.
The existing order's reservation GSI is paginated, then the selected order is
reloaded consistently from the base table. A newly created order can require a
retry while its GSI entry propagates.

Selection covers sessions submitted when they are read, not future submissions.
Individual guest operations are not transactionally serialized with group
finalization: an in-flight `SubmitCartAsync` can validate the open group, pause
during dish validation, and save after confirmation closes it. That guest can
receive success even though its dishes were absent from the earlier collection.
The existing session save checks existence, not group status at commit time.
This patch protects Order updates and the submitted-session selection contract;
it does not promise cross-operation serializability with late guest submissions.
This participant-finalization limitation remains for a separate business-flow fix.

## Compatibility and deployment

`POST /orders/{reservationId}/submit` retains its existing request, response,
all-participants-submitted validation and new-order creation behavior. The Angular
Owner workflow continues to call that legacy endpoint. This change adds no Angular
page or API caller and does not alter guest-token authentication.

The new route is registered in RouteRegistry and the root Syndicate
`deployment_resources.json`, with the existing Cognito authorizer, Lambda proxy
integration and CORS conventions. Deploying it later requires the backend code and
the new API Gateway resource/method together. No IAM or Cognito changes are included.
Upgrade all order writers and drain old in-flight mutations before activating the
new endpoint. Do not mix old unconditional writers with this release through a
weighted alias or an older integration target; they could overwrite confirmation.

The current Angular group workflow does not provision the existing order required
by the new endpoint. Do not switch it to this API without first defining that order
creation flow. In particular, creating an order with the same consolidated items
immediately before submission would add those quantities twice.

Existing-order mutations also retain the original persisted snapshot and save
conditionally. This covers edits, waiter dish decisions, cancellation, individual
order submission and unsubmission. It prevents an older edit from saving after a
successful group confirmation and replacing its quantities or status. Sequential
business behavior and response contracts are preserved; stale writes return 400.
New-order creation and the legacy group-create transaction remain separate.

Implementation and validation are local only. No AWS deployment or live DynamoDB
writes are part of this change.

## Validation

Full backend suite: **287 passed, 0 failed, 0 skipped**, including 70 new cases.
The full solution Release build succeeded with **0 errors and 52 warnings**.
Existing nullable/obsolete-API/test-analyzer warnings and package security advisories
remain; package dependencies were not changed.

```powershell
dotnet test backend/restaurant-backend-app/Tests/APIHandler.Tests.csproj --no-restore --verbosity minimal -p:NuGetAudit=false
dotnet build backend/restaurant-backend-app/restaurant-backend-app.sln --configuration Release --no-restore --verbosity minimal -p:NuGetAudit=false
```

Coverage includes exact JSON and HTTP errors, submitted/unsubmitted filtering,
aggregation and existing-order merging, raw DynamoDBContext serialization, paginated
consistent reads, conditional failures, transaction infrastructure errors, concurrent
duplicate confirmations and reverse races with a legacy HTTP edit. The concurrency
tests evaluate actual request conditions in an atomic in-memory DynamoDB double;
they do not access AWS. Live authenticated integration remains unverified.
The final read-only review found no further defects within the required contract
and confirmed the reverse stale-Order guard; it also identified the late guest
submission limitation documented above.

## Files created or modified

All paths below are relative to `backend/restaurant-backend-app`.

Created:

- `dnapp/lambdas/api-handler/Handlers/GroupReservations/SubmitGroupReservationHandler.cs`
- `dnapp/lambdas/api-handler/Models/GroupReservation/SubmitGroupReservationRequest.cs`
- `dnapp/lambdas/api-handler/Models/GroupReservation/ConsolidatedDishResponse.cs`
- `dnapp/lambdas/api-handler/Services/Orders/OrderService.GroupSubmission.cs`
- `dnapp/lambdas/api-handler/Repositories/Orders/OrderSubmissionSnapshot.cs`
- `dnapp/lambdas/api-handler/Repositories/Orders/OrderRepository.GroupSubmission.cs`
- `dnapp/lambdas/api-handler/Repositories/Orders/OrderRepository.ConditionalUpdates.cs`
- `dnapp/lambdas/api-handler/Repositories/GuestSessions/GuestSessionRepository.GroupSubmission.cs`
- `Tests/GroupSubmissionTestData.cs`
- `Tests/Handlers/SubmitGroupReservationHandlerTests.cs`
- `Tests/Services/GroupSubmissionServiceTests.cs`
- `Tests/Repositories/GroupSubmissionRepositoryTests.cs`
- `Tests/Repositories/GroupSubmissionSessionReadTests.cs`
- `Tests/Repositories/GroupSubmissionSerializationTests.cs`
- `docs/GroupPreOrderSubmission.md`

Modified:

- `deployment_resources.json` — new Cognito/proxy/CORS route.
- `dnapp/lambdas/api-handler/Routing/RouteRegistry.cs` — new handler registration.
- `dnapp/lambdas/api-handler/Services/Orders/IOrderService.cs` — new submit operation.
- `dnapp/lambdas/api-handler/Services/Orders/OrderService.cs` — snapshot guards for
  existing-order mutations; legacy group-create business flow retained.
- `dnapp/lambdas/api-handler/Repositories/Orders/IOrderRepository.cs` — snapshot,
  conditional-save and existing-order group-submit operations.
- `dnapp/lambdas/api-handler/Repositories/Orders/OrderRepository.cs` — partial
  declaration so new operations live in focused files.
- `dnapp/lambdas/api-handler/Repositories/GuestSessions/IGuestSessionRepository.cs`
  — consistent terminal-read operation.
- `Tests/Services/OrderServiceTests.cs` — snapshot setup and conditional-save
  verification while retaining existing behavior assertions.
