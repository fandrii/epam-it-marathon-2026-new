import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { DishListResponse } from '@app/models/dishes/dish-list-response.model';
import { DishesService } from '@app/services/dishes/dishes.service';
import { PreOrdersService } from '@app/services/pre-orders/pre-orders.service';
import { GuestSessionResponse, GuestSessionsService } from '@app/services/guest-sessions/guest-sessions.service';
import { ReservationService } from '@app/services/reservation/reservation.service';
import { ReservationStatus } from '@app/models/reservations/reservation.model';
import { ToastService } from '@app/services/toast/toast.service';
import { BehaviorSubject, of, Subject, throwError } from 'rxjs';
import { MenuPageComponent } from './menu-page.component';

describe('MenuPageComponent', () => {
  let component: MenuPageComponent;
  let fixture: ComponentFixture<MenuPageComponent>;
  let dishesService: jasmine.SpyObj<DishesService>;
  let preOrdersService: jasmine.SpyObj<PreOrdersService>;
  let toastService: jasmine.SpyObj<ToastService>;
  let guestSessionsService: jasmine.SpyObj<GuestSessionsService>;
  let reservationService: jasmine.SpyObj<ReservationService>;
  let params: BehaviorSubject<Record<string, string>>;
  let session: GuestSessionResponse;

  const dish: DishListResponse = {
    id: 'dish-1',
    name: 'Caesar Salad',
    imageUrl: '',
    price: 180,
    state: 'Available',
    weight: '250g',
    dishType: 'Salad',
    description: '',
    calories: '200',
    proteins: '10',
    carbohydrates: '20',
    fats: '8',
    vitamins: '',
  };

  beforeEach(async () => {
    dishesService = jasmine.createSpyObj<DishesService>('DishesService', ['getDishes']);
    dishesService.getDishes.and.returnValue(of([]));
    preOrdersService = jasmine.createSpyObj<PreOrdersService>('PreOrdersService', [
      'addDishToPreOrderByReservationId',
      'submitPreOrderByReservationId',
    ]);
    toastService = jasmine.createSpyObj<ToastService>('ToastService', ['error', 'success']);
    params = new BehaviorSubject<Record<string, string>>({});
    session = {
      name: 'Owner', items: [{ dish: { id: dish.id, name: dish.name, price: dish.price }, quantity: 2 }],
      total: 360, isSubmitted: false,
    };
    guestSessionsService = jasmine.createSpyObj<GuestSessionsService>('GuestSessionsService', [
      'getOwnerGuestSessionId', 'getOwnerSession', 'getOwnerMenu', 'addOwnerDish',
      'removeOwnerDish', 'submitOwnerCart', 'getGroupSessions', 'getGroupStatus',
    ]);
    guestSessionsService.getOwnerGuestSessionId.and.returnValue(of({ guestSessionId: 'owner-session' }));
    guestSessionsService.getOwnerSession.and.callFake(() => of(session));
    guestSessionsService.getOwnerMenu.and.returnValue(of({ dishes: { Appetizer: [{ ...dish, selectable: true }] } }));
    guestSessionsService.getGroupSessions.and.returnValue(of({ participants: [
      { id: 'owner-session', name: 'Owner', type: 'Owner', items: session.items, total: 360, isSubmitted: true },
    ], total: 360 }));
    guestSessionsService.getGroupStatus.and.returnValue(of({ status: 'Open' }));
    reservationService = jasmine.createSpyObj<ReservationService>('ReservationService', ['getReservationsForUser$']);
    reservationService.getReservationsForUser$.and.returnValue(of([{
      id: 'reservation-1', groupReservationId: 'group-1', customerId: 'owner-1',
      tableId: 'table-1', waiterId: 'waiter-1', guestCount: 3, status: ReservationStatus.Reserved,
      startTime: new Date(), endTime: new Date(), tableCapacity: 4, locationAddress: 'Main Street',
    }]));

    await TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: ActivatedRoute, useValue: { queryParams: params } },
        { provide: PreOrdersService, useValue: preOrdersService },
        { provide: GuestSessionsService, useValue: guestSessionsService },
        { provide: ReservationService, useValue: reservationService },
        { provide: ToastService, useValue: toastService },
      ],
      imports: [MenuPageComponent],
    })
      .overrideComponent(MenuPageComponent, {
        set: { providers: [{ provide: DishesService, useValue: dishesService }] },
      })
      .compileComponents();

    fixture = TestBed.createComponent(MenuPageComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });

  it('shows the server response body when adding a dish to an owner pre-order fails', () => {
    component.preOrderReservationId = 'reservation-1';
    preOrdersService.addDishToPreOrderByReservationId.and.returnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            error: { error: 'Dish is not available.' },
            status: 400,
            statusText: 'Bad Request',
          })
      )
    );

    component.addDishToCart(dish);

    expect(toastService.error).toHaveBeenCalledWith('Dish is not available.', 'Error');
  });

  function openOwnerMenu(): void {
    params.next({ preOrder: 'true', groupPreOrder: 'true', reservationId: 'reservation-1' });
    fixture.detectChanges();
  }

  it('retrieves the owner session and restaurant menu and renders the existing cart with server totals', () => {
    openOwnerMenu();
    expect(guestSessionsService.getOwnerGuestSessionId).toHaveBeenCalledOnceWith('reservation-1');
    expect(guestSessionsService.getOwnerMenu).toHaveBeenCalledOnceWith('owner-session');
    expect(component.filteredDishes.map(item => item.id)).toEqual([dish.id]);

    const cartButton = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
      .find(button => button.textContent?.includes('My pre-order'))!;
    cartButton.click();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="dialog"]')?.textContent).toContain('Caesar Salad');
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('$360.00');
  });

  it('adds and removes dishes only through the owner session and refreshes quantities and totals', () => {
    openOwnerMenu();
    guestSessionsService.addOwnerDish.and.returnValue(of({}));
    session = { ...session, items: [{ ...session.items[0], quantity: 3 }], total: 540 };
    component.addDishToCart(dish);
    expect(guestSessionsService.addOwnerDish).toHaveBeenCalledOnceWith('owner-session', dish.id, 1);
    expect(component.ownerSession?.total).toBe(540);
    expect(component.ownerCartItems[0].qty).toBe(3);

    guestSessionsService.removeOwnerDish.and.returnValue(of({}));
    session = { ...session, items: [], total: 0 };
    component.removeOwnerDish(dish.id);
    expect(guestSessionsService.removeOwnerDish).toHaveBeenCalledOnceWith('owner-session', dish.id, 3);
    expect(component.ownerCartItems).toEqual([]);
    expect(component.ownerSession?.total).toBe(0);
    expect(preOrdersService.addDishToPreOrderByReservationId).not.toHaveBeenCalled();
  });

  it('submits the individual owner cart then opens the consolidated group summary', () => {
    openOwnerMenu();
    guestSessionsService.submitOwnerCart.and.returnValue(of({}));
    component.ownerCartOpen = true;

    component.submitOwnerPreOrder();
    fixture.detectChanges();

    expect(guestSessionsService.submitOwnerCart).toHaveBeenCalledOnceWith('owner-session');
    expect(component.ownerSession?.isSubmitted).toBeTrue();
    expect(component.ownerCartOpen).toBeFalse();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Total for all participants');
    expect(preOrdersService.submitPreOrderByReservationId).not.toHaveBeenCalled();
    component.submitOwnerPreOrder();
    expect(guestSessionsService.submitOwnerCart).toHaveBeenCalledTimes(1);
  });

  it('restores a submitted owner cart on reload without requesting the closed menu or allowing edits', () => {
    session = { ...session, isSubmitted: true };
    openOwnerMenu();
    component.changeOwnerQuantity(dish.id, 1);
    component.submitOwnerPreOrder();

    expect(guestSessionsService.getOwnerMenu).not.toHaveBeenCalled();
    expect(guestSessionsService.addOwnerDish).not.toHaveBeenCalled();
    expect(guestSessionsService.submitOwnerCart).not.toHaveBeenCalled();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Your individual pre-order is submitted.');
  });

  it('blocks individual submission of an empty cart', () => {
    session = { ...session, items: [], total: 0 };
    openOwnerMenu();
    component.submitOwnerPreOrder();
    expect(guestSessionsService.submitOwnerCart).not.toHaveBeenCalled();
  });

  it('keeps a rejected owner lookup out of the participant and regular-order flows', () => {
    guestSessionsService.getOwnerGuestSessionId.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 403, error: { message: 'Only the reservation owner can access this session.' },
    })));
    openOwnerMenu();
    component.addDishToCart(dish);
    component.submitOwnerPreOrder();

    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Only the reservation owner can access this session.');
    expect(guestSessionsService.getOwnerMenu).not.toHaveBeenCalled();
    expect(guestSessionsService.submitOwnerCart).not.toHaveBeenCalled();
    expect(preOrdersService.addDishToPreOrderByReservationId).not.toHaveBeenCalled();
  });

  it('preserves the cart and server message on failed individual submission', () => {
    openOwnerMenu();
    guestSessionsService.submitOwnerCart.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 409, error: { message: 'This pre-order session has expired.' },
    })));
    component.submitOwnerPreOrder();

    expect(component.ownerSession?.isSubmitted).toBeFalse();
    expect(component.ownerCartItems[0].qty).toBe(2);
    expect(component.ownerError).toBe('This pre-order session has expired.');
    expect(component.groupSummaryOpen).toBeFalse();
  });

  it('cancels an old owner lookup when the reservation context changes', () => {
    const lookup = new Subject<{ guestSessionId: string }>();
    guestSessionsService.getOwnerGuestSessionId.and.returnValue(lookup);
    openOwnerMenu();
    params.next({});
    lookup.next({ guestSessionId: 'stale-owner-session' });

    expect(guestSessionsService.getOwnerSession).not.toHaveBeenCalled();
    expect(component.isOwnerGroupPreOrder).toBeFalse();
  });

  it('completes the owner cart and consolidated confirmation from the existing menu interface', () => {
    openOwnerMenu();
    guestSessionsService.addOwnerDish.and.returnValue(of({}));
    session = { ...session, items: [{ ...session.items[0], quantity: 3 }], total: 540 };
    component.addDishToCart(dish);
    guestSessionsService.removeOwnerDish.and.returnValue(of({}));
    session = { ...session, items: [{ ...session.items[0], quantity: 2 }], total: 360 };
    component.changeOwnerQuantity(dish.id, 1, true);
    guestSessionsService.getGroupSessions.and.returnValue(of({ participants: [
      { id: 'owner-session', name: 'Owner', type: 'Owner', items: session.items, total: 360, isSubmitted: true },
      { id: 'guest-session', name: 'Participant', type: 'Guest', items: session.items, total: 360, isSubmitted: true },
    ], total: 720 }));
    guestSessionsService.submitOwnerCart.and.returnValue(of({}));
    preOrdersService.submitPreOrderByReservationId.and.returnValue(of({}));
    component.ownerCartOpen = true;
    fixture.detectChanges();

    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('.drawer .submit')!.click();
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;
    expect(element.textContent).toContain('Participant');
    expect(element.textContent).toContain('$720.00');
    Array.from(element.querySelectorAll('button'))
      .find(button => button.textContent?.trim() === 'Confirm group pre-order')!.click();
    fixture.detectChanges();

    expect(guestSessionsService.submitOwnerCart).toHaveBeenCalledOnceWith('owner-session');
    expect(preOrdersService.submitPreOrderByReservationId).toHaveBeenCalledOnceWith('reservation-1');
    expect(element.textContent).toContain('The consolidated group pre-order is confirmed.');
  });
});
