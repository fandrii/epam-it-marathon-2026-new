import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import {
  ReservationCustomer,
  ReservationStatusCustomer,
} from '@app/models/reservations/reservation.model';
import { Roles } from '@app/shared/constants/roles';
import { CardReservationComponent } from './card-reservation.component';

describe('CardReservationComponent', () => {
  let component: CardReservationComponent;
  let fixture: ComponentFixture<CardReservationComponent>;
  const reservation: ReservationCustomer = {
    id: 'reservation-1',
    tableId: 'table-1',
    startTime: new Date('2026-11-13T12:00:00Z'),
    endTime: new Date('2026-11-13T14:00:00Z'),
    guestCount: 3,
    status: ReservationStatusCustomer.Reserved,
    restaurantName: 'Green & Tasty',
    locationAddress: 'Main Street',
    waiterId: 'waiter-1',
    guestInviteLinkAvailable: true,
    groupReservationId: 'group-1',
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      providers: [provideRouter([])],
      imports: [CardReservationComponent],
    }).compileComponents();

    fixture = TestBed.createComponent(CardReservationComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('r', reservation);
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('shows the group pre-order action to the owner when an invite is available', () => {
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('View group pre-order');
  });

  it('does not show the group pre-order action to waiters', () => {
    fixture.componentRef.setInput('r', { ...reservation, guestInviteLinkAvailable: true });
    fixture.componentRef.setInput('role', Roles.WAITER);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain(
      'View group pre-order'
    );
  });

  it('keeps group review accessible after invitation links are disabled', () => {
    fixture.componentRef.setInput('r', { ...reservation, guestInviteLinkAvailable: false });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('View group pre-order');
  });

  it('opens the existing menu in owner group mode for a group reservation', () => {
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

    component.onPreOrder();

    expect(navigate).toHaveBeenCalledWith(['/menu'], {
      queryParams: jasmine.objectContaining({
        preOrder: 'true', groupPreOrder: 'true', reservationId: reservation.id,
      }),
    });
  });

  it('preserves the regular pre-order flow for reservations without a group', () => {
    fixture.componentRef.setInput('r', { ...reservation, groupReservationId: undefined });
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

    component.onPreOrder();

    expect(navigate).toHaveBeenCalledWith(['/menu'], {
      queryParams: jasmine.objectContaining({ groupPreOrder: null, reservationId: reservation.id }),
    });
  });
});
