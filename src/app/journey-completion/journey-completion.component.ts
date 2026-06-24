import { Component, OnDestroy, OnInit } from '@angular/core';
import { Subscription, catchError, of, switchMap, timer } from 'rxjs';
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
  private completionPolling?: Subscription;
  private closeCountdownStarted = false;

  session: AwfaceJourneySession | null = null;
  result: AwfaceCompletionResult | null = null;
  closeProgress = 0;
  closeBlocked = false;

  constructor(public awfaceService: AwfaceService) {}

  ngOnInit(): void {
    this.session = this.awfaceService.getActiveSession();
    this.result = this.awfaceService.getCompletionResult() || {
      status: 'PENDING',
      message: 'A prova de vida foi enviada e está em processo de validação.',
    };

    if (this.success) {
      this.startCloseCountdown();
      return;
    }

    if (this.pending) {
      this.startCompletionPolling();
    }
  }

  ngOnDestroy(): void {
    if (this.progressTimer) {
      clearInterval(this.progressTimer);
    }

    if (this.closeTimer) {
      clearTimeout(this.closeTimer);
    }

    this.completionPolling?.unsubscribe();
  }

  get success(): boolean {
    return this.result?.status === 'SUCCESS';
  }

  get pending(): boolean {
    return this.result?.status === 'PENDING';
  }

  get failed(): boolean {
    return this.result?.status === 'FAILED';
  }

  getTenantLogo(): string {
    return this.awfaceService.getJourneyLogoSource(this.session);
  }

  private startCloseCountdown(): void {
    if (this.closeCountdownStarted) {
      return;
    }

    this.closeCountdownStarted = true;
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

  private startCompletionPolling(): void {
    const journeyId = this.session?.id || this.awfaceService.getActiveJourneyId();
    if (!journeyId) {
      this.result = {
        status: 'FAILED',
        message: 'A jornada não foi encontrada para consultar a comunicação final.',
      };
      this.awfaceService.saveCompletionResult(this.result);
      return;
    }

    this.completionPolling?.unsubscribe();
    this.completionPolling = timer(0, 1000).pipe(
      switchMap(() => this.awfaceService.getCompletionStatus(journeyId).pipe(
        catchError(() => of<AwfaceCompletionResult | null>(null))
      ))
    ).subscribe(result => {
      if (!result) {
        return;
      }

      this.result = result;
      this.awfaceService.saveCompletionResult(result);

      if (result.status === 'PENDING') {
        return;
      }

      this.completionPolling?.unsubscribe();
      if (result.status === 'SUCCESS') {
        this.startCloseCountdown();
      }
    });
  }
}
