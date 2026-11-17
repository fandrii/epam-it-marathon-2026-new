import { ComponentFixture, TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { Reservation, ReservationStatus } from '@app/models/reservations/reservation.model';
import { GuestInviteService } from '@app/services/reservation/guest-invite.service';
import { ToastService } from '@app/services/toast/toast.service';
import { ReservationConfirmedComponent } from './reservation-confirmed.component';

const reservation: Reservation = {
  id: 'reservation-1', tableId: 'table-1', customerId: 'owner-1', waiterId: 'waiter-1',
  startTime: new Date(2026, 10, 20, 18), endTime: new Date(2026, 10, 20, 20),
  guestCount: 3, status: ReservationStatus.Reserved, locationAddress: '48 Rustaveli Avenue',
  tableCapacity: 4, groupReservationId: 'group-1',
};

describe('ReservationConfirmedComponent', () => {
  let fixture: ComponentFixture<ReservationConfirmedComponent>;
  let inviteService: jasmine.SpyObj<GuestInviteService>;
  let toastService: jasmine.SpyObj<ToastService>;

  beforeEach(async () => {
    inviteService = jasmine.createSpyObj('GuestInviteService', ['getInviteToken$']);
    inviteService.getInviteToken$.and.returnValue(of({ token: 'group-token' }));
    toastService = jasmine.createSpyObj('ToastService', ['success', 'error']);
    await TestBed.configureTestingModule({
      imports: [ReservationConfirmedComponent],
      providers: [
        { provide: GuestInviteService, useValue: inviteService },
        { provide: ToastService, useValue: toastService },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ReservationConfirmedComponent);
  });

  it('shows the existing invitation for the edited group reservation', () => {
    fixture.componentRef.setInput('reservation', reservation);
    fixture.detectChanges();

    expect(inviteService.getInviteToken$).toHaveBeenCalledOnceWith('reservation-1');
    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector<HTMLInputElement>('#edited-reservation-invite')?.value)
      .toBe(`${window.location.origin}/join/group-token`);
  });

  it('copies the invitation link and shows Copied feedback', async () => {
    fixture.componentRef.setInput('reservation', reservation);
    fixture.detectChanges();
    const writeText = spyOn(navigator.clipboard, 'writeText').and.resolveTo();

    await fixture.componentInstance.onCopyInvite();
    fixture.detectChanges();

    expect(writeText).toHaveBeenCalledWith(`${window.location.origin}/join/group-token`);
    expect(fixture.nativeElement.querySelector('.copy-invite')?.textContent).toContain('Copied!');
  });

  it('does not request or display an invitation for an individual reservation', () => {
    fixture.componentRef.setInput('reservation', { ...reservation, groupReservationId: undefined, guestCount: 1 });
    fixture.detectChanges();

    expect(inviteService.getInviteToken$).not.toHaveBeenCalled();
    expect(fixture.nativeElement.querySelector('.invite-section')).toBeNull();
  });
});
