import { ComponentFixture, TestBed } from '@angular/core/testing';

import { JornadaInitComponent } from './jornada-init.component';

describe('JornadaInitComponent', () => {
  let component: JornadaInitComponent;
  let fixture: ComponentFixture<JornadaInitComponent>;

  beforeEach(() => {
    TestBed.configureTestingModule({
      declarations: [JornadaInitComponent]
    });
    fixture = TestBed.createComponent(JornadaInitComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
