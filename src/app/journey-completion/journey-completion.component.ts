import { Component, OnDestroy, OnInit } from '@angular/core';
import { AwfaceService } from '../awface/awface.service';
import { AwfaceCompletionResult, AwfaceJourneySession } from '../awface/models';

@Component({
  selector: 'app-journey-completion',
  templateUrl: './journey-completion.component.html',
  styleUrls: ['./journey-completion.component.scss'],
})
export class JourneyCompletionComponent implements OnInit, OnDestroy {
  private readonly closeDelayMs = 6000;
  private progressTimer?: ReturnType<typeof setInterval>;
  private closeTimer?: ReturnType<typeof setTimeout>;

  session: AwfaceJourneySession | null = null;
  result: AwfaceCompletionResult | null = null;
  closeProgress = 0;
  closeBlocked = false;

  constructor(public awfaceService: AwfaceService) {}

  ngOnInit(): void {
    this.session = this.awfaceService.getActiveSession();
    this.result = this.awfaceService.getCompletionResult();

    if (this.success) {
      this.startCloseCountdown();
    }
  }

  ngOnDestroy(): void {
    if (this.progressTimer) {
      clearInterval(this.progressTimer);
    }

    if (this.closeTimer) {
      clearTimeout(this.closeTimer);
    }
  }

  get success(): boolean {
    return this.result?.status === 'SUCCESS';
  }

  getTenantLogo(): string {
    return this.awfaceService.getJourneyLogoSource(this.session);
  }

  private startCloseCountdown(): void {
    const startedAt = Date.now();

    this.progressTimer = setInterval(() => {
      const elapsed = Date.now() - startedAt;
      this.closeProgress = Math.min(100, Math.round((elapsed / this.closeDelayMs) * 100));
    }, 100);

    this.closeTimer = setTimeout(() => {
      if (this.progressTimer) {
        clearInterval(this.progressTimer);
      }

      this.closeProgress = 100;
      window.close();

      setTimeout(() => {
        this.closeBlocked = true;
      }, 300);
    }, this.closeDelayMs);
  }
}
