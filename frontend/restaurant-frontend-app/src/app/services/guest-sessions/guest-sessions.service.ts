import { inject, Injectable } from '@angular/core';
import { HttpClient, HttpHeaders } from '@angular/common/http';
import { API_BASE_URL } from '@app/shared/config/api-base-url.token';

export interface GroupSessionDish {
    id: string;
    name: string;
    price: number;
}

export interface GroupSessionOrderItem {
    dish: GroupSessionDish;
    quantity: number;
}

export interface GroupSessionParticipant {
    id: string | null;
    type: 'Owner' | 'Guest';
    name: string;
    items: GroupSessionOrderItem[];
    total: number;
    isSubmitted: boolean;
}

export interface GroupSessionsResponse {
    participants: GroupSessionParticipant[];
    total: number;
}

export interface CreateGuestSessionRequest {
    groupReservationId: string;
    guestName: string;
    idempotencyKey?: string;
}

export interface CreateGuestSessionResponse {
    SessionId: string;
    SessionToken: string;
}

export interface GuestMenuDish {
    id: string;
    name: string;
    description: string;
    price: number;
    weight?: string;
    calories?: string;
    proteins?: string;
    fats?: string;
    carbohydrates?: string;
    vitamins?: string;
    imageUrl?: string | null;
    state: string;        // "Available" | "On Stop"
    selectable: boolean;  // true/false
}

export type GuestMenuCategoryKey = string;

export interface GuestMenuResponse {
    dishes: Record<GuestMenuCategoryKey, GuestMenuDish[]>;
}

export interface GuestSessionResponse {
    name: string;
    items: GroupSessionOrderItem[];
    total: number;
    isSubmitted: boolean;
}

export interface GroupReservationStatusResponse {
    status: 'Open' | 'Submitted' | 'Closed';
}

@Injectable({ providedIn: 'root' })
export class GuestSessionsService {
    private readonly baseUrl = inject(API_BASE_URL);
    private readonly http = inject(HttpClient);

    getOwnerGuestSessionId(reservationId: string) {
        return this.http.get<{ guestSessionId: string }>(
            `${this.baseUrl}/reservations/${encodeURIComponent(reservationId)}/guest-session-id`
        );
    }

    getGroupStatus(groupReservationId: string) {
        return this.http.get<GroupReservationStatusResponse>(
            `${this.baseUrl}/guest/groups/${encodeURIComponent(groupReservationId)}/status`
        );
    }

    getGroupSessions(groupReservationId: string) {
        return this.http.get<GroupSessionsResponse>(
            `${this.baseUrl}/guest/groups/${encodeURIComponent(groupReservationId)}/sessions`
        );
    }

    deleteGroupSession(groupReservationId: string, guestSessionId: string) {
        return this.http.delete<GroupSessionsResponse>(
            `${this.baseUrl}/guest/groups/${encodeURIComponent(groupReservationId)}/sessions/${encodeURIComponent(guestSessionId)}`
        );
    }

    createSession(body: CreateGuestSessionRequest) {
        return this.http.post<CreateGuestSessionResponse>(`${this.baseUrl}/guest/sessions`, {
            ...body,
            idempotencyKey: body.idempotencyKey ?? crypto.randomUUID(),
        });
    }

    getOwnerSession(guestSessionId: string) {
        return this.http.get<GuestSessionResponse>(
            `${this.baseUrl}/guest/sessions/${encodeURIComponent(guestSessionId)}`
        );
    }

    getOwnerMenu(guestSessionId: string) {
        return this.http.get<GuestMenuResponse>(
            `${this.baseUrl}/guest/sessions/${encodeURIComponent(guestSessionId)}/menu`
        );
    }

    addOwnerDish(guestSessionId: string, dishId: string, quantity: number) {
        return this.http.post(
            `${this.baseUrl}/guest/sessions/${encodeURIComponent(guestSessionId)}/dishes`,
            { dishId, quantity }
        );
    }

    removeOwnerDish(guestSessionId: string, dishId: string, quantity: number) {
        return this.http.request(
            'delete',
            `${this.baseUrl}/guest/sessions/${encodeURIComponent(guestSessionId)}/dishes`,
            { body: { dishId, quantity } }
        );
    }

    submitOwnerCart(guestSessionId: string) {
        return this.http.post(
            `${this.baseUrl}/guest/sessions/${encodeURIComponent(guestSessionId)}/submit`,
            {}
        );
    }

    getMenu(guestSessionId: string, sessionToken: string) {
        return this.http.get<GuestMenuResponse>(
            `${this.baseUrl}/guest/sessions/${guestSessionId}/menu`,
            { headers: this.authHeaders(sessionToken) }
        );
    }

    addDish(guestSessionId: string, sessionToken: string, dishId: string, quantity: number) {
        return this.http.post(
            `${this.baseUrl}/guest/sessions/${guestSessionId}/dishes`,
            { dishId, quantity },
            { headers: this.authHeaders(sessionToken) }
        );
    }

    removeDish(guestSessionId: string, sessionToken: string, dishId: string, quantity: number) {
        return this.http.request(
            'delete',
            `${this.baseUrl}/guest/sessions/${guestSessionId}/dishes`,
            { body: { dishId, quantity }, headers: this.authHeaders(sessionToken) }
        );
    }

    submit(guestSessionId: string, sessionToken: string) {
        return this.http.post(
            `${this.baseUrl}/guest/sessions/${guestSessionId}/submit`,
            {},
            { headers: this.authHeaders(sessionToken) }
        );
    }

    private authHeaders(token: string) {
        return new HttpHeaders({ Authorization: `Bearer ${token}` });
    }
}
