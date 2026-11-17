import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { API_BASE_URL } from '@app/shared/config/api-base-url.token';
import { PreOrdersService } from './pre-orders.service';

describe('PreOrdersService', () => {
  let service: PreOrdersService;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: 'https://test.api' },
      ],
    });
    service = TestBed.inject(PreOrdersService);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('creates a pre-order with the reservation ID and selected dish after no order exists', () => {
    const dish = {
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

    service.addDishToPreOrderByReservationId('reservation-1', dish, 2).subscribe();

    const existingOrdersRequest = httpMock.expectOne('https://test.api/orders');
    expect(existingOrdersRequest.request.method).toBe('GET');
    existingOrdersRequest.flush([]);

    const request = httpMock.expectOne('https://test.api/orders');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({
      reservationId: 'reservation-1',
      orderItems: [{ dishId: 'dish-1', quantity: 2 }],
    });
    request.flush({});
  });

});
