import { CurrencyPipe, DatePipe, NgTemplateOutlet } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  input,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { finalize, forkJoin } from 'rxjs';
import { PreOrdersService } from '@app/services/pre-orders/pre-orders.service';
import { getServerErrorMessage } from '@app/shared/helpers/http-error-helpers';
import {
  GuestSessionsService,
  GroupSessionsResponse,
} from '@app/services/guest-sessions/guest-sessions.service';
import { ModalComponent } from '@app/shared/custom-components/modal/modal.component';
import { Reservation, ReservationCustomer } from '@app/models/reservations/reservation.model';
import { GuestInviteService } from '@app/services/reservation/guest-invite.service';
import { ToastService } from '@app/services/toast/toast.service';

@Component({
  selector: 'app-group-sessions-modal',
  standalone: true,
  imports: [CurrencyPipe, DatePipe, NgTemplateOutlet, ModalComponent, RouterLink],
  templateUrl: './group-sessions-modal.component.html',
  styleUrl: './group-sessions-modal.component.scss',
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class GroupSessionsModalComponent implements OnInit {
  private readonly guestSessionsService = inject(GuestSessionsService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly preOrdersService = inject(PreOrdersService);
  private readonly guestInviteService = inject(GuestInviteService);
  private readonly toastService = inject(ToastService);

  readonly groupReservationId = input.required<string>();
  readonly reservationId = input.required<string>();
  readonly reservation = input<Reservation | ReservationCustomer | null>(null);
  readonly closed = output<void>();

  readonly loading = signal(true);
  readonly error = signal<string | null>(null);
  readonly summary = signal<GroupSessionsResponse | null>(null);
  readonly groupStatus = signal<'Open' | 'Submitted' | 'Closed' | null>(null);
  readonly confirming = signal(false);
  readonly confirmationError = signal<string | null>(null);
  readonly removingSessionId = signal<string | null>(null);
  readonly removalError = signal<string | null>(null);
  readonly pendingRemoval = signal<{ id: string; name: string } | null>(null);
  readonly inviteToken = signal<string | null>(null);
  readonly inviteLoading = signal(false);
  readonly inviteUnavailable = signal(false);
  readonly inviteCopied = signal(false);
  readonly owner = computed(() => this.summary()?.participants.find(p => p.type === 'Owner') ?? null);
  readonly guests = computed(() => this.summary()?.participants.filter(p => p.type === 'Guest') ?? []);
  readonly waitingGuest = computed(() => this.guests().find(p => p.isSubmitted) ?? null);
  readonly statusLabel = computed(() => this.groupStatus() === 'Submitted' ? 'Sent to Restaurant' : 'Created New Pre-Order');
  readonly inviteLink = computed(() => this.inviteToken() ? `${window.location.origin}/join/${this.inviteToken()}` : null);
  readonly canConfirm = computed(() => {
    const summary = this.summary();
    return !this.loading() && !this.error() && !this.confirming() &&
      this.groupStatus() === 'Open' && !!summary?.participants.length &&
      summary.participants.every(participant => participant.isSubmitted) &&
      summary.participants.some(participant => participant.items.length > 0);
  });

  ngOnInit(): void {
    this.loadGroupSessions();
    this.loadInviteLink();
  }

  private loadInviteLink(): void {
    if (!this.reservation()?.groupReservationId) return;
    this.inviteLoading.set(true);
    this.guestInviteService.getInviteToken$(this.reservationId())
      .pipe(takeUntilDestroyed(this.destroyRef), finalize(() => this.inviteLoading.set(false)))
      .subscribe({
        next: ({ token }) => this.inviteToken.set(token),
        error: () => this.inviteUnavailable.set(true),
      });
  }

  async copyInviteLink(): Promise<void> {
    const link = this.inviteLink();
    if (!link) return;
    try {
      await navigator.clipboard.writeText(link);
      this.inviteCopied.set(true);
      this.toastService.success('Share it with your guests.', 'Link copied!');
    } catch {
      this.toastService.error('Could not copy the link. Please copy it manually.');
    }
  }

  loadGroupSessions(): void {
    this.loading.set(true);
    this.error.set(null);
    this.summary.set(null);

    forkJoin({
      summary: this.guestSessionsService.getGroupSessions(this.groupReservationId()),
      status: this.guestSessionsService.getGroupStatus(this.groupReservationId()),
    })
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: ({ summary, status }) => {
          this.summary.set(summary);
          this.groupStatus.set(status.status);
          this.loading.set(false);
        },
        error: (error: HttpErrorResponse) => {
          this.error.set(
            error.status === 403
              ? 'Only the reservation owner can view this group pre-order.'
              : 'Could not load the group pre-order. Please try again.'
          );
          this.loading.set(false);
        },
      });
  }

  confirmGroupPreOrder(): void {
    if (!this.canConfirm()) return;

    this.confirming.set(true);
    this.confirmationError.set(null);
    // The established group flow does not provision an Orders item before confirmation.
    // Keep the legacy atomic create-and-submit endpoint until order provisioning is explicit.
    this.preOrdersService.submitPreOrderByReservationId(this.reservationId())
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.confirming.set(false)),
      )
      .subscribe({
        next: () => this.groupStatus.set('Submitted'),
        error: (error: HttpErrorResponse) => {
          this.confirmationError.set(getServerErrorMessage(error) ||
            'Could not confirm the group pre-order. Refresh the participants and try again.');
          this.loadGroupSessions();
        },
      });
  }

  removeParticipant(participantId: string | null, participantName: string): void {
    if (!participantId || this.groupStatus() !== 'Open' || this.removingSessionId()) return;
    this.pendingRemoval.set({ id: participantId, name: participantName });
  }

  cancelRemoval(): void {
    if (!this.removingSessionId()) this.pendingRemoval.set(null);
  }

  confirmRemoval(): void {
    const pending = this.pendingRemoval();
    if (!pending || this.groupStatus() !== 'Open' || this.removingSessionId()) return;

    this.removingSessionId.set(pending.id);
    this.removalError.set(null);
    this.guestSessionsService.deleteGroupSession(this.groupReservationId(), pending.id)
      .pipe(
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.removingSessionId.set(null)),
      )
      .subscribe({
        next: summary => {
          this.summary.set(summary);
          this.pendingRemoval.set(null);
        },
        error: (error: HttpErrorResponse) => this.removalError.set(
          getServerErrorMessage(error) || 'Could not remove this participant. Refresh and try again.'
        ),
      });
  }

  onClose(): void {
    this.closed.emit();
  }
}
