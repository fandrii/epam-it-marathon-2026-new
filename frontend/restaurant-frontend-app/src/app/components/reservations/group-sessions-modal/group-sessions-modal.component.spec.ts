import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { of, Subject, throwError } from 'rxjs';
import { PreOrdersService } from '@app/services/pre-orders/pre-orders.service';

import {
  GuestSessionsService,
  GroupSessionsResponse,
} from '@app/services/guest-sessions/guest-sessions.service';
import { GroupSessionsModalComponent } from './group-sessions-modal.component';
import { GuestInviteService } from '@app/services/reservation/guest-invite.service';
import { ToastService } from '@app/services/toast/toast.service';

describe('GroupSessionsModalComponent', () => {
  let fixture: ComponentFixture<GroupSessionsModalComponent>;
  let guestSessionsService: jasmine.SpyObj<GuestSessionsService>;
  let preOrdersService: jasmine.SpyObj<PreOrdersService>;
  let guestInviteService: jasmine.SpyObj<GuestInviteService>;

  const groupSessions: GroupSessionsResponse = {
    participants: [
      {
        id: 'session-1',
        type: 'Owner',
        name: 'Owner',
        items: [{ dish: { id: 'dish-1', name: 'Caesar Salad', price: 180 }, quantity: 2 }],
        total: 360,
        isSubmitted: true,
      },
      {
        id: 'session-guest',
        type: 'Guest',
        name: 'Andrii',
        items: [{ dish: { id: 'dish-2', name: 'Tom Yum', price: 220 }, quantity: 1 }],
        total: 220,
        isSubmitted: false,
      },
    ],
    total: 580,
  };

  beforeEach(async () => {
    guestSessionsService = jasmine.createSpyObj<GuestSessionsService>('GuestSessionsService', [
      'getGroupSessions',
      'getGroupStatus',
      'deleteGroupSession',
    ]);
    guestSessionsService.getGroupSessions.and.returnValue(of(groupSessions));
    guestSessionsService.getGroupStatus.and.returnValue(of({ status: 'Open' }));
    preOrdersService = jasmine.createSpyObj<PreOrdersService>('PreOrdersService', [
      'submitPreOrderByReservationId',
    ]);
    guestInviteService = jasmine.createSpyObj<GuestInviteService>('GuestInviteService', ['getInviteToken$']);
    guestInviteService.getInviteToken$.and.returnValue(of({ token: 'invite-1' }));

    await TestBed.configureTestingModule({
      imports: [GroupSessionsModalComponent],
      providers: [
        provideRouter([]),
        { provide: GuestSessionsService, useValue: guestSessionsService },
        { provide: PreOrdersService, useValue: preOrdersService },
        { provide: GuestInviteService, useValue: guestInviteService },
        { provide: ToastService, useValue: jasmine.createSpyObj('ToastService', ['success', 'error']) },
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(GroupSessionsModalComponent);
    fixture.componentRef.setInput('groupReservationId', 'group-1');
    fixture.componentRef.setInput('reservationId', 'reservation-1');
    fixture.componentRef.setInput('reservation', {
      id: 'reservation-1', groupReservationId: 'group-1', locationAddress: '48 Rustaveli Avenue',
      startTime: new Date(2026, 10, 20, 18), endTime: new Date(2026, 10, 20, 20), guestCount: 2,
    });
    fixture.detectChanges();
  });

  it('loads and displays participants, dishes, submission state, and totals', () => {
    const element = fixture.nativeElement as HTMLElement;

    expect(guestSessionsService.getGroupSessions).toHaveBeenCalledOnceWith('group-1');
    expect(element.textContent).toContain('Caesar Salad');
    expect(element.textContent).toContain('Andrii');
    expect(element.textContent).toContain('Guest');
    expect(element.textContent).toContain('Tom Yum');
    expect(element.textContent).toContain('My Pre-Order');
    expect(element.textContent).toContain('Guest: Andrii');
    expect(element.textContent).toContain('Subtotal');
    expect(element.textContent).toContain('Grand Total');
    expect(element.textContent).toContain('$580.00');
  });

  it('shows an empty state when no participants have joined', () => {
    guestSessionsService.getGroupSessions.and.returnValue(of({ participants: [], total: 0 }));

    fixture.componentInstance.loadGroupSessions();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'No participants have joined this group order yet.'
    );
  });

  it('shows a permission error when the backend rejects a non-owner', () => {
    guestSessionsService.getGroupSessions.and.returnValue(
      throwError(() => new HttpErrorResponse({ status: 403 }))
    );

    fixture.componentInstance.loadGroupSessions();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain(
      'Only the reservation owner can view this group pre-order.'
    );
  });

  function allSubmitted(): void {
    guestSessionsService.getGroupSessions.and.returnValue(of({
      ...groupSessions,
      participants: groupSessions.participants.map(participant => ({ ...participant, isSubmitted: true })),
    }));
    fixture.componentInstance.loadGroupSessions();
    fixture.detectChanges();
  }

  it('blocks legacy group confirmation until every participant has submitted', () => {
    expect(fixture.componentInstance.canConfirm()).toBeFalse();
    expect(preOrdersService.submitPreOrderByReservationId).not.toHaveBeenCalled();
  });

  it('confirms through the existing endpoint and blocks repeated clicks and confirmation after success', () => {
    allSubmitted();
    const response = new Subject<object>();
    preOrdersService.submitPreOrderByReservationId.and.returnValue(response);

    const button = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
      .find(button => button.textContent?.includes('Submit Pre-Order'))!;
    expect(button.disabled).toBeFalse();
    button.click();
    fixture.componentInstance.confirmGroupPreOrder();
    expect(preOrdersService.submitPreOrderByReservationId).toHaveBeenCalledOnceWith('reservation-1');

    response.next({});
    response.complete();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Sent to Restaurant');
    expect(fixture.componentInstance.canConfirm()).toBeFalse();
  });

  it('recognizes a previously confirmed group on reopening', () => {
    guestSessionsService.getGroupStatus.and.returnValue(of({ status: 'Submitted' }));
    allSubmitted();
    fixture.componentInstance.confirmGroupPreOrder();

    expect(fixture.componentInstance.canConfirm()).toBeFalse();
    expect(preOrdersService.submitPreOrderByReservationId).not.toHaveBeenCalled();
  });

  it('refreshes readiness after a server rejection and keeps the server error visible', () => {
    allSubmitted();
    preOrdersService.submitPreOrderByReservationId.and.returnValue(throwError(() =>
      new HttpErrorResponse({ status: 400, error: { message: 'All participant pre-orders must be submitted.' } })));
    guestSessionsService.getGroupSessions.and.returnValue(of(groupSessions));

    fixture.componentInstance.confirmGroupPreOrder();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('All participant pre-orders must be submitted.');
    expect(fixture.componentInstance.canConfirm()).toBeFalse();
  });

  it('links back to the owner cart using the existing menu route', () => {
    const link = (fixture.nativeElement as HTMLElement).querySelector<HTMLAnchorElement>('a')!;
    expect(link.href).toContain('/menu?');
    expect(link.href).toContain('reservationId=reservation-1');
    expect(link.href).toContain('groupPreOrder=true');
  });

  it('shows remove only for guests and replaces the summary with the server response', () => {
    const updated: GroupSessionsResponse = {
      participants: [groupSessions.participants[0]],
      total: groupSessions.participants[0].total,
    };
    guestSessionsService.deleteGroupSession.and.returnValue(of(updated));

    const buttons = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.remove-participant'));
    expect(buttons.length).toBe(1);
    buttons[0].click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.confirm-dialog')?.textContent).toContain(
      'Are you sure you want to remove Andrii and their pre-order from this reservation?'
    );
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.danger-button')!.click();
    fixture.detectChanges();

    expect(guestSessionsService.deleteGroupSession).toHaveBeenCalledOnceWith('group-1', 'session-guest');
    expect(fixture.componentInstance.summary()).toEqual(updated);
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Andrii');
  });

  it('does not delete when confirmation is cancelled', () => {
    fixture.componentInstance.removeParticipant('session-guest', 'Andrii');
    fixture.detectChanges();
    fixture.componentInstance.cancelRemoval();
    expect(guestSessionsService.deleteGroupSession).not.toHaveBeenCalled();
    expect(fixture.componentInstance.pendingRemoval()).toBeNull();
  });

  it('hides removal after group submission', () => {
    guestSessionsService.getGroupStatus.and.returnValue(of({ status: 'Submitted' }));
    fixture.componentInstance.loadGroupSessions();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('.remove-participant')).toBeNull();
  });

  it('prevents duplicate removals and displays server errors', () => {
    const response = new Subject<GroupSessionsResponse>();
    guestSessionsService.deleteGroupSession.and.returnValue(response);

    fixture.componentInstance.removeParticipant('session-guest', 'Andrii');
    fixture.componentInstance.confirmRemoval();
    fixture.componentInstance.confirmRemoval();
    expect(guestSessionsService.deleteGroupSession).toHaveBeenCalledTimes(1);
    response.error(new HttpErrorResponse({ status: 400, error: { error: 'Group is no longer open.' } }));
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Group is no longer open.');
  });
});
