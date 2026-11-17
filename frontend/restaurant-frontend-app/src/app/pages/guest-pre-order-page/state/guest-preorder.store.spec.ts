import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { of, Subject, throwError } from 'rxjs';
import { GuestInvite } from '@app/models/reservations/guest-invite.model';
import { GuestInviteService } from '@app/services/reservation/guest-invite.service';
import { CreateGuestSessionResponse, GuestSessionsService } from '@app/services/guest-sessions/guest-sessions.service';
import { GuestPreorderStore } from './guest-preorder.store';

const invite: GuestInvite = {
  GroupReservationId: 'group-1', LocationId: 'location-1', RestaurantName: 'Restaurant',
  LocationAddress: 'Main Street', StartTime: '2026-11-16T12:00:00Z', EndTime: '2026-11-16T14:00:00Z',
  MaxGuestCount: 2, TableNumber: 1, InviterName: 'Owner',
};
const response: CreateGuestSessionResponse = { SessionId: 'guest-session', SessionToken: 'guest-token' };

describe('GuestPreorderStore creation retries', () => {
  let store: GuestPreorderStore;
  let sessions: jasmine.SpyObj<GuestSessionsService>;
  let invites: jasmine.SpyObj<GuestInviteService>;

  beforeEach(async () => {
    localStorage.clear();
    sessions = jasmine.createSpyObj<GuestSessionsService>('GuestSessionsService', ['createSession', 'getMenu']);
    sessions.getMenu.and.returnValue(of({ dishes: {} }));
    invites = jasmine.createSpyObj<GuestInviteService>('GuestInviteService', ['getInvite$']);
    invites.getInvite$.and.returnValue(of(invite));
    TestBed.configureTestingModule({ providers: [
      { provide: GuestSessionsService, useValue: sessions },
      { provide: GuestInviteService, useValue: invites },
    ] });
    store = TestBed.inject(GuestPreorderStore);
    store.initContext('invite-token');
    await Promise.resolve();
  });

  afterEach(() => localStorage.clear());

  it('reuses one key after a failed request and resumes using the returned guest token', async () => {
    sessions.createSession.and.returnValue(throwError(() => new HttpErrorResponse({ status: 0 })));
    await store.join('Guest');
    const first = sessions.createSession.calls.mostRecent().args[0];
    expect(first.idempotencyKey).toBeTruthy();
    expect(store.guestName()).toBe('');

    sessions.createSession.and.returnValue(of(response));
    await store.join('Guest');

    expect(sessions.createSession.calls.mostRecent().args[0]).toEqual(first);
    expect(store.guestSessionId()).toBe(response.SessionId);
    expect(sessions.getMenu).toHaveBeenCalledWith(response.SessionId, response.SessionToken);
    expect(localStorage.getItem('guestPreorder:invite-token:pendingCreation')).toBeNull();
  });

  it('retains the exact pending attempt across a browser reload', async () => {
    sessions.createSession.and.returnValue(throwError(() => new HttpErrorResponse({ status: 0 })));
    await store.join('Guest');
    const original = sessions.createSession.calls.mostRecent().args[0];
    TestBed.resetTestingModule();
    sessions.createSession.and.returnValue(of(response));
    TestBed.configureTestingModule({ providers: [
      { provide: GuestSessionsService, useValue: sessions },
      { provide: GuestInviteService, useValue: invites },
    ] });
    const restored = TestBed.inject(GuestPreorderStore);
    restored.initContext('invite-token');
    await Promise.resolve();
    await Promise.resolve();

    expect(sessions.createSession.calls.mostRecent().args[0]).toEqual(original);
    expect(restored.guestSessionId()).toBe(response.SessionId);
  });

  it('uses a fresh key for an explicitly requested new pre-order even with the same name', async () => {
    sessions.createSession.and.returnValue(of(response));
    await store.join('Guest');
    const first = sessions.createSession.calls.mostRecent().args[0];
    await store.createNewPreorder();
    const second = sessions.createSession.calls.mostRecent().args[0];

    expect(first.guestName).toBe(second.guestName);
    expect(first.idempotencyKey).not.toBe(second.idempotencyKey);
  });

  it('prevents double-clicks from creating parallel attempts', async () => {
    const pending = new Subject<CreateGuestSessionResponse>();
    sessions.createSession.and.returnValue(pending);
    const first = store.join('Guest');
    await store.join('Guest');
    expect(sessions.createSession).toHaveBeenCalledTimes(1);
    pending.next(response);
    pending.complete();
    await first;
  });
});
