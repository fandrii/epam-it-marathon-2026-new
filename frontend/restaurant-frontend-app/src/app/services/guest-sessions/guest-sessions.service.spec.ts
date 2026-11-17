import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API_BASE_URL } from '@app/shared/config/api-base-url.token';
import { GuestSessionsService, GroupSessionsResponse } from './guest-sessions.service';

const BASE_URL = 'https://test.api';

describe('GuestSessionsService', () => {
  let service: GuestSessionsService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: BASE_URL },
      ],
    });
    service = TestBed.inject(GuestSessionsService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('preserves the client idempotency key in the creation body', () => {
    const body = { groupReservationId: 'group-1', guestName: 'Guest', idempotencyKey: 'attempt-1' };
    service.createSession(body).subscribe();
    const req = httpMock.expectOne(`${BASE_URL}/guest/sessions`);
    expect(req.request.body).toEqual(body);
    req.flush({ SessionId: 'session-1', SessionToken: 'token' });
  });

  it('generates different keys for distinct legacy-client creation calls with identical names', () => {
    const body = { groupReservationId: 'group-1', guestName: 'Guest' };
    service.createSession(body).subscribe();
    service.createSession(body).subscribe();
    const requests = httpMock.match(`${BASE_URL}/guest/sessions`);
    expect(requests[0].request.body.idempotencyKey).toMatch(/^[0-9a-f-]{36}$/);
    expect(requests[0].request.body.idempotencyKey).not.toBe(requests[1].request.body.idempotencyKey);
    requests.forEach(req => req.flush({ SessionId: 'session', SessionToken: 'token' }));
  });

  it('looks up the existing owner session through the reservation endpoint', () => {
    let id: string | undefined;
    service.getOwnerGuestSessionId('reservation/1').subscribe(result => id = result.guestSessionId);

    const req = httpMock.expectOne(`${BASE_URL}/reservations/reservation%2F1/guest-session-id`);
    expect(req.request.method).toBe('GET');
    req.flush({ guestSessionId: 'owner-1' });
    expect(id).toBe('owner-1');
  });

  it('reads the server group confirmation state', () => {
    service.getGroupStatus('group-1').subscribe(result => expect(result.status).toBe('Submitted'));
    const req = httpMock.expectOne(`${BASE_URL}/guest/groups/group-1/status`);
    expect(req.request.method).toBe('GET');
    req.flush({ status: 'Submitted' });
  });

  it('gets group participants and totals for the owner view', () => {
    const response: GroupSessionsResponse = {
      participants: [
        {
          id: 'session-1',
          type: 'Guest',
          name: 'Andrii',
          items: [{ dish: { id: 'dish-1', name: 'Caesar Salad', price: 180 }, quantity: 2 }],
          total: 360,
          isSubmitted: true,
        },
      ],
      total: 360,
    };
    let actual: GroupSessionsResponse | undefined;

    service.getGroupSessions('group-1').subscribe((value) => (actual = value));

    const req = httpMock.expectOne(`${BASE_URL}/guest/groups/group-1/sessions`);
    expect(req.request.method).toBe('GET');
    req.flush(response);

    expect(actual).toEqual(response);
  });

  it('encodes the group reservation id in the request URL', () => {
    service.getGroupSessions('group/1').subscribe();

    const req = httpMock.expectOne(`${BASE_URL}/guest/groups/group%2F1/sessions`);
    expect(req.request.method).toBe('GET');
    req.flush({ participants: [], total: 0 });
  });

  it('deletes a group participant and returns the updated snapshot', () => {
    const response: GroupSessionsResponse = { participants: [], total: 0 };
    let actual: GroupSessionsResponse | undefined;
    service.deleteGroupSession('group/1', 'session/2').subscribe(value => actual = value);

    const req = httpMock.expectOne(`${BASE_URL}/guest/groups/group%2F1/sessions/session%2F2`);
    expect(req.request.method).toBe('DELETE');
    req.flush(response);
    expect(actual).toEqual(response);
  });

  it('gets the owner session without constructing a guest-token header', () => {
    service.getOwnerSession('owner/session').subscribe();

    const req = httpMock.expectOne(`${BASE_URL}/guest/sessions/owner%2Fsession`);
    expect(req.request.method).toBe('GET');
    expect(req.request.headers.has('Authorization')).toBeFalse();
    req.flush({ name: 'Owner', items: [], total: 0, isSubmitted: false });
  });

  it('gets the owner menu through the shared session endpoint', () => {
    service.getOwnerMenu('owner-1').subscribe();

    const req = httpMock.expectOne(`${BASE_URL}/guest/sessions/owner-1/menu`);
    expect(req.request.method).toBe('GET');
    req.flush({ dishes: {} });
  });

  it('adds or updates an owner dish quantity', () => {
    service.addOwnerDish('owner-1', 'dish-1', 2).subscribe();

    const req = httpMock.expectOne(`${BASE_URL}/guest/sessions/owner-1/dishes`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ dishId: 'dish-1', quantity: 2 });
    req.flush({});
  });

  it('removes an owner dish quantity', () => {
    service.removeOwnerDish('owner-1', 'dish-1', 1).subscribe();

    const req = httpMock.expectOne(`${BASE_URL}/guest/sessions/owner-1/dishes`);
    expect(req.request.method).toBe('DELETE');
    expect(req.request.body).toEqual({ dishId: 'dish-1', quantity: 1 });
    req.flush({});
  });

  it('submits the owner cart through the shared submit endpoint', () => {
    service.submitOwnerCart('owner-1').subscribe();

    const req = httpMock.expectOne(`${BASE_URL}/guest/sessions/owner-1/submit`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({});
  });
});
