import { Component, OnInit } from '@angular/core';
import { AwfaceLivenessEngine } from '../awface/models';
import { AwfaceService } from '../awface/awface.service';

@Component({
  selector: 'app-journey-engine',
  template: `
    <app-facetec-v9 *ngIf="engine === 'V9'"></app-facetec-v9>
    <app-facetec-v10 *ngIf="engine === 'V10'"></app-facetec-v10>
  `
})
export class JourneyEngineComponent implements OnInit {
  engine: AwfaceLivenessEngine = 'V10';

  constructor(private readonly awfaceService: AwfaceService) {}

  ngOnInit(): void {
    this.engine = this.awfaceService.getActiveSession()?.livenessEngine
      || this.awfaceService.getActiveLivenessEngine();
  }
}
