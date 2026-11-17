import { ComponentFixture, TestBed } from '@angular/core/testing';
import { GuestCartDrawerComponent } from './guest-cart-drawer.component';

describe('GuestCartDrawerComponent', () => {
  let fixture: ComponentFixture<GuestCartDrawerComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({ imports: [GuestCartDrawerComponent] }).compileComponents();
    fixture = TestBed.createComponent(GuestCartDrawerComponent);
    fixture.componentRef.setInput('open', true);
    fixture.componentRef.setInput('items', [{ dishId: 'dish-1', title: 'Salad', qty: 2, price: 12.5 }]);
    fixture.componentRef.setInput('total', 25);
    fixture.detectChanges();
  });

  it('preserves editable participant cart controls by default', () => {
    const inc = spyOn(fixture.componentInstance.inc, 'emit');
    const submit = spyOn(fixture.componentInstance.submitted, 'emit');
    const element = fixture.nativeElement as HTMLElement;
    element.querySelector<HTMLButtonElement>('[aria-label="Increase quantity"]')!.click();
    element.querySelector<HTMLButtonElement>('.submit')!.click();

    expect(inc).toHaveBeenCalledWith('dish-1');
    expect(submit).toHaveBeenCalled();
    expect(element.textContent).toContain('$25.00');
  });

  it('displays submitted owner carts with every mutation control disabled', () => {
    fixture.componentRef.setInput('readOnly', true);
    fixture.detectChanges();
    const element = fixture.nativeElement as HTMLElement;

    for (const selector of ['.qty-btn', '.trash', '.submit']) {
      expect(Array.from(element.querySelectorAll<HTMLButtonElement>(selector))
        .every(button => button.disabled)).toBeTrue();
    }
    expect(element.textContent).toContain('Pre-order submitted');
    expect(element.textContent).toContain('Salad');
  });
});
