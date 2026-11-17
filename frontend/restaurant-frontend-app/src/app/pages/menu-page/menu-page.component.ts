import { HttpErrorResponse } from '@angular/common/http';
import {Component, OnInit, ChangeDetectionStrategy, DestroyRef, inject} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { finalize, forkJoin, of, Subscription, switchMap } from 'rxjs';
import { DishCardComponent } from '@app/shared/custom-components/dish-card/dish-card.component';
import { DishesService } from '@app/services/dishes/dishes.service';
import { DishListResponse } from '@app/models/dishes/dish-list-response.model';
import { MainCoverImageComponent } from '@app/shared/custom-components/main-cover-image/main-cover-image.component';
import { RouterModule, ActivatedRoute, Router } from '@angular/router';
import { DishSort } from '@app/models/enums/dish-sort.model';
import { DishType } from '@app/models/enums/dish-type.model';
import { DishSortingLabels, DishTypeLabels } from '@app/shared/mappers/dish-enum.mapper';
import { DishModalComponent } from '@app/components/dish-modal/dish-modal.component';
import { PreOrdersService } from '@app/services/pre-orders/pre-orders.service';
import {ToastService} from '@app/services/toast/toast.service';
import { getServerErrorMessage } from '@app/shared/helpers/http-error-helpers';
import { GuestMenuDish, GuestMenuResponse, GuestSessionResponse, GuestSessionsService } from '@app/services/guest-sessions/guest-sessions.service';
import { ReservationService } from '@app/services/reservation/reservation.service';
import { GuestCartDrawerComponent } from '../guest-pre-order-page/ui/guest-cart-drawer/guest-cart-drawer.component';
import { GroupSessionsModalComponent } from '@app/components/reservations/group-sessions-modal/group-sessions-modal.component';
import { Reservation, ReservationCustomer } from '@app/models/reservations/reservation.model';

@Component({
  selector: 'app-menu-page',
  standalone: true,
  imports: [
    RouterModule,
    DishCardComponent,
    MainCoverImageComponent,
    DishModalComponent,
    GuestCartDrawerComponent,
    GroupSessionsModalComponent,
  ],
  templateUrl: './menu-page.component.html',
  styleUrl: './menu-page.component.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
  providers: [DishesService],
})
export class MenuPageComponent implements OnInit {
  private toast = inject(ToastService);
  dishes: DishListResponse[] = [];
  filteredDishes: DishListResponse[] = [];

  isPreOrderEnabled = false;
  preOrderReservationId?: string;
  preOrderReservationLabel?: string;
  isOwnerGroupPreOrder = false;
  ownerSessionId?: string;
  ownerGroupId?: string;
  ownerReservation?: Reservation | ReservationCustomer;
  ownerSession?: GuestSessionResponse;
  ownerLoading = false;
  ownerBusy = false;
  ownerCartStale = false;
  ownerError: string | null = null;
  ownerCartOpen = false;
  groupSummaryOpen = false;
  private menuLoad?: Subscription;

  get ownerCartItems() {
    return (this.ownerSession?.items ?? []).map(item => ({
      dishId: item.dish.id,
      title: item.dish.name,
      price: item.dish.price,
      qty: item.quantity,
      imageUrl: this.dishes.find(dish => dish.id === item.dish.id)?.imageUrl || 'assets/img/dish.png',
    }));
  }

  sortOptions = Object.values(DishSort);
  sortLabels = DishSortingLabels;

  menuCategories = Object.values(DishType);
  menuCategoryLabels = DishTypeLabels;

  selectedCategory: DishType | null = null; // initially show all
  selectedSort: DishSort = DishSort.PopularityDescending;

  private readonly dishesService = inject(DishesService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private  readonly orderService = inject(PreOrdersService);
  private readonly guestSessionsService = inject(GuestSessionsService);
  private readonly reservationService = inject(ReservationService);
  private readonly destroyRef = inject(DestroyRef);

  ngOnInit(): void {
    this.route.queryParams.pipe(takeUntilDestroyed(this.destroyRef)).subscribe((params) => {
      const preOrder = params['preOrder'];
      this.isPreOrderEnabled = !!preOrder;
      this.isOwnerGroupPreOrder = params['groupPreOrder'] === 'true';
      this.preOrderReservationId = undefined;
      this.ownerCartOpen = false;
      this.groupSummaryOpen = false;
      if (this.isPreOrderEnabled) {
        this.preOrderReservationId = params['reservationId'];
        this.preOrderReservationLabel = params['reservationLabel'];
        if (!this.preOrderReservationId) {
          console.error('Pre-order is enabled but no reservation ID provided.');
          this.toast.error('Pre-order is enabled but no reservation ID provided.', 'Error');
          this.isPreOrderEnabled = false;
        }
      }

      const category = params['category'] as DishType;
      const sort = params['sort'] as DishSort;

      this.selectedCategory =
        category && Object.values(DishType).includes(category) ? category : null;
      this.selectedSort =
        sort && Object.values(DishSort).includes(sort) ? sort : DishSort.PopularityDescending;

      this.loadDishes();
    });
  }

  loadDishes(): void {
    this.menuLoad?.unsubscribe();
    if (this.isOwnerGroupPreOrder) {
      this.loadOwnerPreOrder();
      return;
    }
    const category = this.selectedCategory ?? undefined;
    const sort = this.selectedSort;

    this.menuLoad = this.dishesService.getDishes(category, sort)
      .pipe(takeUntilDestroyed(this.destroyRef)).subscribe((data) => {
      this.dishes = data.map((dish) => ({
        ...dish,
        imageUrl: dish.imageUrl || 'assets/img/dish.png',
        name: dish.name || 'Unknown Dish',
        price: dish.price || 0,
        weight: dish.weight || 'N/A',
        description: dish.description || 'N/A',
        state: dish.state || 'N/A',
        dishType: dish.dishType || 'N/A',
        calories: dish.calories || '0',
        proteins: dish.proteins || '0',
        fats: dish.fats || '0',
        carbohydrates: dish.carbohydrates || '0',
        vitamins: dish.vitamins || '0',
      }));

      this.filteredDishes = this.dishes;
    });
  }

  onCategorySelected(category: DishType): void {
    this.selectedCategory = this.selectedCategory === category ? null : category;

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        category: this.selectedCategory || null,
        sort: this.selectedSort,
      },
      queryParamsHandling: 'merge',
    });
  }

  onSortChanged(event: Event): void {
    const selectElement = event.target as HTMLSelectElement;
    const sort = selectElement.value as DishSort;
    this.selectedSort = sort;

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: {
        category: this.selectedCategory || null,
        sort: this.selectedSort,
      },
      queryParamsHandling: 'merge',
      replaceUrl: true,
    });
  }

  selectedDish: DishListResponse | null = null;

  openModal(dish: DishListResponse) {
    this.selectedDish = dish;
  }

  closeModal() {
    this.selectedDish = null;
  }

  addDishToCart(dish: DishListResponse): void {
    if (this.isOwnerGroupPreOrder) {
      this.changeOwnerQuantity(dish.id, 1);
      return;
    }
    this.orderService
      .addDishToPreOrderByReservationId(this.preOrderReservationId!, dish, 1)
      .subscribe({
        next: () => {
          this.toast.success('Dish added to cart successfully!', 'Success');
        },
        error: (err: unknown) => {
          const serverMessage =
            err instanceof HttpErrorResponse ? getServerErrorMessage(err) : '';
          this.toast.error(serverMessage || 'Could not add dish to pre-order.', 'Error');
        },
      });
  }

  private loadOwnerPreOrder(): void {
    this.ownerSessionId = undefined;
    this.ownerSession = undefined;
    this.ownerGroupId = undefined;
    this.ownerReservation = undefined;
    this.ownerBusy = false;
    this.ownerCartStale = false;
    this.ownerError = null;
    this.dishes = [];
    this.filteredDishes = [];
    const reservationId = this.preOrderReservationId;
    if (!reservationId) {
      this.ownerError = 'Select a group reservation from My Reservations to open your pre-order.';
      return;
    }

    this.ownerLoading = true;
    this.menuLoad = forkJoin({
      owner: this.guestSessionsService.getOwnerGuestSessionId(reservationId),
      reservations: this.reservationService.getReservationsForUser$(),
    }).pipe(
      switchMap(({ owner, reservations }) => {
        const reservation = reservations.find(item => item.id === reservationId);
        if (!reservation?.groupReservationId) {
          throw new Error('This reservation has no group pre-order.');
        }
        this.ownerSessionId = owner.guestSessionId;
        this.ownerGroupId = reservation.groupReservationId;
        this.ownerReservation = reservation;
        this.preOrderReservationLabel ||= reservation.locationAddress;
        return this.guestSessionsService.getOwnerSession(owner.guestSessionId);
      }),
      switchMap(session => {
        this.ownerSession = session;
        // Submitted sessions remain readable even after group confirmation closes the menu.
        return session.isSubmitted ? of<GuestMenuResponse>({ dishes: {} }) :
          this.guestSessionsService.getOwnerMenu(this.ownerSessionId!);
      }),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => this.ownerLoading = false),
    ).subscribe({
      next: menu => {
        this.dishes = Object.entries(menu.dishes).flatMap(([category, dishes]) =>
          dishes.map(dish => this.ownerMenuDish(dish, category)));
        this.filteredDishes = this.dishes.filter(dish =>
          !this.selectedCategory || dish.dishType === this.selectedCategory);
        if (this.selectedSort === DishSort.PriceAscending || this.selectedSort === DishSort.PriceDescending) {
          const direction = this.selectedSort === DishSort.PriceAscending ? 1 : -1;
          this.filteredDishes.sort((a, b) => direction * (a.price - b.price));
        }
      },
      error: error => {
        this.ownerError = this.ownerRequestError(error, 'Could not load your pre-order. Please try again.');
      },
    });
  }

  changeOwnerQuantity(dishId: string, quantity: number, remove = false): void {
    const sessionId = this.ownerSessionId;
    if (!sessionId || !this.ownerSession || this.ownerSession.isSubmitted ||
      this.ownerBusy || this.ownerLoading || this.ownerCartStale) return;

    this.ownerBusy = true;
    this.ownerError = null;
    const request = remove
      ? this.guestSessionsService.removeOwnerDish(sessionId, dishId, quantity)
      : this.guestSessionsService.addOwnerDish(sessionId, dishId, quantity);
    request.pipe(
      switchMap(() => {
        this.ownerCartStale = true;
        return this.guestSessionsService.getOwnerSession(sessionId);
      }),
      takeUntilDestroyed(this.destroyRef),
      finalize(() => { if (this.ownerSessionId === sessionId) this.ownerBusy = false; }),
    ).subscribe({
      next: session => {
        if (this.ownerSessionId !== sessionId) return;
        this.ownerSession = session;
        this.ownerCartStale = false;
      },
      error: error => {
        if (this.ownerSessionId === sessionId)
          this.ownerError = this.ownerRequestError(error, 'Could not update your pre-order. Please try again.');
      },
    });
  }

  removeOwnerDish(dishId: string): void {
    const item = this.ownerSession?.items.find(item => item.dish.id === dishId);
    if (item) this.changeOwnerQuantity(dishId, item.quantity, true);
  }

  submitOwnerPreOrder(): void {
    const sessionId = this.ownerSessionId;
    if (!sessionId || !this.ownerSession?.items.length || this.ownerSession.isSubmitted ||
      this.ownerBusy || this.ownerLoading || this.ownerCartStale) return;

    this.ownerBusy = true;
    this.ownerError = null;
    this.guestSessionsService.submitOwnerCart(sessionId)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => { if (this.ownerSessionId === sessionId) this.ownerBusy = false; }),
      ).subscribe({
        next: () => {
          if (this.ownerSessionId !== sessionId) return;
          this.ownerSession = { ...this.ownerSession!, isSubmitted: true };
          this.ownerCartOpen = false;
          this.groupSummaryOpen = true;
        },
        error: error => {
          if (this.ownerSessionId === sessionId)
            this.ownerError = this.ownerRequestError(error, 'Could not submit your pre-order. Please try again.');
        },
      });
  }

  private ownerRequestError(error: unknown, fallback: string): string {
    return error instanceof HttpErrorResponse ? getServerErrorMessage(error) || fallback : fallback;
  }

  private ownerMenuDish(dish: GuestMenuDish, category: string): DishListResponse {
    return {
      ...dish,
      imageUrl: dish.imageUrl || 'assets/img/dish.png',
      dishType: category,
      state: dish.selectable ? dish.state : 'OutOfStock',
      weight: dish.weight ?? '', calories: dish.calories ?? '', proteins: dish.proteins ?? '',
      carbohydrates: dish.carbohydrates ?? '', fats: dish.fats ?? '', vitamins: dish.vitamins ?? '',
    };
  }
}
