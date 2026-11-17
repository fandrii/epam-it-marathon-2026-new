import { HttpClient, HttpContext, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ToastService } from '../services/toast/toast.service';
import { AuthenticationService } from './services/authentication.service';
import { SKIP_ERROR_TOAST } from './skip-error-toast.context';
import { tokenInterceptor } from './token.interceptor';

const URL = 'https://test.api/reservations';

describe('tokenInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let toastService: jasmine.SpyObj<ToastService>;
  let authService: jasmine.SpyObj<AuthenticationService>;

  beforeEach(() => {
    toastService = jasmine.createSpyObj<ToastService>('ToastService', ['error']);
    authService = jasmine.createSpyObj<AuthenticationService>('AuthenticationService', [
      'getTokens',
    ]);
    authService.getTokens.and.returnValue(null);

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([tokenInterceptor])),
        provideHttpClientTesting(),
        { provide: ToastService, useValue: toastService },
        { provide: AuthenticationService, useValue: authService },
      ],
    });

    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('shows the server message in a toast for a 400 response', () => {
    http.post(URL, {}).subscribe({ error: () => undefined });

    httpMock
      .expectOne(URL)
      .flush({ message: 'Overlaps' }, { status: 400, statusText: 'Bad Request' });

    expect(toastService.error).toHaveBeenCalledWith('Overlaps', 'Error');
  });

  it('attaches the Cognito ID token to the owner POST /orders request', () => {
    authService.getTokens.and.returnValue({
      idToken: 'cognito-id-token',
      accessToken: 'cognito-access-token',
    });
    const url = 'https://test.api/orders';
    const body = {
      reservationId: 'reservation-1',
      orderItems: [{ dishId: 'dish-1', quantity: 1 }],
    };

    http.post(url, body).subscribe();

    const req = httpMock.expectOne(url);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    expect(req.request.headers.get('Authorization')).toBe('Bearer cognito-id-token');
    req.flush({});
  });

  it('adds Cognito authorization to the owner group sessions endpoint', () => {
    authService.getTokens.and.returnValue({
      idToken: 'cognito-id-token',
      accessToken: 'cognito-access-token',
    });
    const url = 'https://test.api/guest/groups/group-1/sessions';

    http.get(url).subscribe();

    const req = httpMock.expectOne(url);
    expect(req.request.headers.get('Authorization')).toBe('Bearer cognito-id-token');
    req.flush({ participants: [], total: 0 });
  });

  it('preserves an explicit guest token on participant-session requests', () => {
    authService.getTokens.and.returnValue({
      idToken: 'cognito-id-token',
      accessToken: 'cognito-access-token',
    });
    const url = 'https://test.api/guest/sessions/session-1/menu';

    http.get(url, {
      headers: { Authorization: 'Bearer guest-session-token' },
    }).subscribe();

    const req = httpMock.expectOne(url);
    expect(req.request.headers.get('Authorization')).toBe('Bearer guest-session-token');
    req.flush({ dishes: {} });
  });

  it('adds Cognito authorization to an owner session request without a guest token', () => {
    authService.getTokens.and.returnValue({
      idToken: 'cognito-id-token',
      accessToken: 'cognito-access-token',
    });
    const url = 'https://test.api/guest/sessions/owner-session/menu';

    http.get(url).subscribe();

    const req = httpMock.expectOne(url);
    expect(req.request.headers.get('Authorization')).toBe('Bearer cognito-id-token');
    req.flush({ dishes: {} });
  });

  it('uses Cognito for the owner session lookup, individual submit, and consolidated confirmation', () => {
    authService.getTokens.and.returnValue({ idToken: 'owner-id-token', accessToken: 'owner-access-token' });
    const base = 'https://test.api';

    http.get(`${base}/reservations/reservation-1/guest-session-id`).subscribe();
    http.post(`${base}/guest/sessions/owner-1/submit`, {}).subscribe();
    http.post(`${base}/orders/reservation-1/submit`, {}).subscribe();

    for (const url of [
      `${base}/reservations/reservation-1/guest-session-id`,
      `${base}/guest/sessions/owner-1/submit`,
      `${base}/orders/reservation-1/submit`,
    ]) {
      const req = httpMock.expectOne(url);
      expect(req.request.headers.get('Authorization')).toBe('Bearer owner-id-token');
      req.flush({});
    }
  });

  it('does not show a toast but still rethrows when SKIP_ERROR_TOAST is set', () => {
    let receivedStatus: number | undefined;
    http
      .post(URL, {}, { context: new HttpContext().set(SKIP_ERROR_TOAST, true) })
      .subscribe({ error: (error) => (receivedStatus = error.status) });

    httpMock
      .expectOne(URL)
      .flush({ message: 'Overlaps' }, { status: 400, statusText: 'Bad Request' });

    expect(toastService.error).not.toHaveBeenCalled();
    expect(receivedStatus).toBe(400);
  });
});
