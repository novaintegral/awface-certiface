import { Component, NgZone, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { environment } from 'src/environments/environment';
import { ThemeHelpers } from 'src/assets/utilities/ThemeHelpers';
import { SampleAppUtilities } from 'src/assets/utilities/SampleAppUtilities';
import { FacetecV9Processor } from 'src/assets/facetec-v9/FacetecV9Processor';
import { AwfaceJourneySession } from '../awface/models';
import { AwfaceService } from '../awface/awface.service';
import { FacetecV9Service } from './facetec-v9.service';

@Component({
  selector: 'app-facetec-v9',
  templateUrl: './facetec-v9.component.html',
  styleUrls: ['./facetec-v9.component.css']
})
export class FacetecV9Component implements OnInit, OnDestroy {
  FacetecLogo = '/assets/img/logo_certiface_trans.png';
  status = '';
  activeSession: AwfaceJourneySession | null = null;
  journeySubjectName = '';
  journeyTypeLabel = '';
  isAutonomousJourney = false;
  isLivenessReady = false;
  livenessButtonLabel = 'Iniciar prova de vida';

  private appkey = '';
  private sdkV9: any;
  private requiresFreshAppkey = false;

  constructor(
    private readonly router: Router,
    private readonly ngZone: NgZone,
    private readonly facetecV9Service: FacetecV9Service,
    public readonly awfaceService: AwfaceService
  ) {}

  async ngOnInit(): Promise<void> {
    this.appkey = this.awfaceService.getRuntimeAppkey() || '';
    this.activeSession = this.awfaceService.getActiveSession();
    this.journeySubjectName = this.activeSession?.subject.fullName
      || this.awfaceService.getActiveJourneySubjectName()
      || '';
    const journeyType = this.activeSession?.journeyType || this.awfaceService.getActiveJourneyType();
    this.journeyTypeLabel = journeyType ? this.awfaceService.getJourneyLabel(journeyType) : '';
    this.isAutonomousJourney = this.awfaceService.getActiveJourneySource() === 'AUTONOMOUS';
    this.FacetecLogo = this.awfaceService.getJourneyLogoSource(this.activeSession);

    await this.captureDeviceLocation();
    this.displayStatus('Preparando a câmera...');

    if (!this.appkey) {
      this.displayStatus('Sua appkey é inválida. Recomece a jornada.');
      return;
    }

    try {
      this.sdkV9 = await this.loadFaceTecV9();
      const productionKey = await this.facetecV9Service.getProductionKey(this.appkey);
      this.initializeSdk(productionKey);
    } catch (error) {
      console.error(error);
      this.displayStatus('Não foi possível inicializar a prova de vida.');
    }
  }

  ngOnDestroy(): void {
    if (this.sdkV9?.deinitialize) {
      this.sdkV9.deinitialize(() => undefined);
    }
  }

  async showLiveness3D(): Promise<void> {
    this.isLivenessReady = false;
    this.livenessButtonLabel = 'Iniciar prova de vida';
    SampleAppUtilities.fadeOutMainUIAndPrepareForSession();

    try {
      if (this.requiresFreshAppkey && this.activeSession) {
        this.appkey = await firstValueFrom(this.awfaceService.issueAppkey(this.activeSession, true));
        this.requiresFreshAppkey = false;
      }

      const userAgent = this.sdkV9.createFaceTecAPIUserAgentString('');
      const sessionToken = await this.createV9SessionToken(userAgent);

      new FacetecV9Processor(
        sessionToken,
        this.sdkV9,
        this.appkey,
        this.awfaceService.getRuntimeDeviceLocation(),
        {
          completed: () => this.ngZone.run(() => {
            this.displayStatus('Prova de Vida em processo de validação... <span class="awface-status-spinner"></span>');
            this.navigateToCompletion();
          }),
          rejected: () => this.ngZone.run(() => this.prepareRetry()),
          blocked: () => this.ngZone.run(() => this.navigateToBlockedCompletion()),
          interrupted: () => this.ngZone.run(() => this.prepareInterruptedRetry()),
          failed: message => this.ngZone.run(() => this.prepareFailure(message)),
        }
      );
    } catch (error) {
      console.error(error);
      this.prepareFailure(error instanceof Error ? error.message : 'Não foi possível iniciar a prova de vida.');
    }
  }

  deleteAppKey(): void {
    this.awfaceService.clearRuntimeState();
    this.router.navigateByUrl('/journey-start');
  }

  cancelProcess(): void {
    this.awfaceService.clearRuntimeState();
    window.close();
  }

  private async createV9SessionToken(userAgent: string): Promise<string> {
    try {
      return await this.facetecV9Service.getSessionToken(this.appkey, userAgent);
    } catch (error) {
      if (!this.activeSession || !this.isUnauthorizedSessionTokenError(error)) {
        throw error;
      }

      this.appkey = await firstValueFrom(this.awfaceService.issueAppkey(this.activeSession, true));
      this.requiresFreshAppkey = false;
      return await this.facetecV9Service.getSessionToken(this.appkey, userAgent);
    }
  }

  private isUnauthorizedSessionTokenError(error: unknown): boolean {
    const message = error instanceof Error
      ? error.message
      : JSON.stringify(error || '');

    return message.includes('NAO AUTORIZADO') || message.includes('401');
  }

  private initializeSdk(productionKey: string): void {
    this.sdkV9.setResourceDirectory('/assets/9.7.109/core-sdk/FaceTecSDK.js/resources');
    this.sdkV9.setImagesDirectory('/assets/9.7.109/core-sdk/FaceTec_images');

    this.sdkV9.initializeInProductionMode(
      productionKey,
      environment.DeviceKeyIdentifier,
      environment.PublicFaceScanEncryptionKey,
      async (initializedSuccessfully: boolean) => {
        this.ngZone.run(() => {
          if (!initializedSuccessfully) {
            this.displayStatus(
              this.sdkV9.getFriendlyDescriptionForFaceTecSDKStatus(this.sdkV9.getStatus())
            );
            SampleAppUtilities.showMainUI();
            return;
          }

        });

        if (!initializedSuccessfully) {
          return;
        }

        await this.configureSdkExperience();
        this.ngZone.run(() => {
          this.isLivenessReady = true;
          SampleAppUtilities.showMainUI();
          this.displayStatus('Inicializado com sucesso');
        });
      }
    );
  }

  private async configureSdkExperience(): Promise<void> {
    const localization = await import('src/assets/9.7.109/core-sdk-optional/FaceTecStrings.pt-br.js');
    this.sdkV9.configureLocalization(localization.default);
    ThemeHelpers.setAppTheme(ThemeHelpers.getCurrentTheme());
  }

  private prepareRetry(): void {
    this.requiresFreshAppkey = false;
    this.livenessButtonLabel = 'Tente novamente';
    this.isLivenessReady = true;
    this.displayStatus('<strong class="awface-neutral-status">Prova de Vida reprovada.</strong>');
    this.restoreMainUi();
  }

  private prepareInterruptedRetry(): void {
    this.livenessButtonLabel = 'Iniciar prova de vida';
    this.isLivenessReady = true;
    this.displayStatus('<strong class="awface-neutral-status">Saiu da tela inteira sem concluir a prova de vida.</strong>');
    this.restoreMainUi();
  }

  private prepareFailure(message: string): void {
    this.livenessButtonLabel = 'Tente novamente';
    this.isLivenessReady = true;
    this.displayStatus(`<strong class="awface-neutral-status">${message}</strong>`);
    this.restoreMainUi();
  }

  private restoreMainUi(): void {
    SampleAppUtilities.showMainUI();
    window.setTimeout(() => {
      this.isLivenessReady = true;
      document.getElementById('liveness-button')?.removeAttribute('disabled');
    }, 900);
  }

  private displayStatus(message: string): void {
    this.status = message;
    const statusElement = document.getElementById('status');
    if (statusElement) {
      statusElement.innerHTML = message;
    }
  }

  private captureDeviceLocation(): Promise<void> {
    if (!navigator.geolocation) {
      return Promise.resolve();
    }

    return new Promise(resolve => {
      navigator.geolocation.getCurrentPosition(
        position => {
          this.awfaceService.setRuntimeDeviceLocation({
            latitude: position.coords.latitude,
            longitude: position.coords.longitude,
            accuracy: position.coords.accuracy,
            capturedAt: new Date().toISOString(),
          });
          resolve();
        },
        () => resolve(),
        { enableHighAccuracy: true, timeout: 10000, maximumAge: 0 }
      );
    });
  }

  private async loadFaceTecV9(): Promise<any> {
    const currentSdk = (window as any).FaceTecSDK;
    if (currentSdk?.version?.().startsWith('9.')) {
      return currentSdk;
    }

    (window as any).FaceTecSDK = undefined;
    await this.loadScript('assets/9.7.109/core-sdk/FaceTecSDK.js/FaceTecSDK.js');

    const sdk = (window as any).FaceTecSDK;
    if (!sdk) {
      throw new Error('FaceTec SDK V9 não carregou corretamente.');
    }

    return sdk;
  }

  private loadScript(src: string): Promise<void> {
    return new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = src;
      script.async = false;
      script.onload = () => resolve();
      script.onerror = () => reject(new Error(`Erro ao carregar ${src}`));
      document.body.appendChild(script);
    });
  }

  private navigateToBlockedCompletion(): void {
    this.awfaceService.saveCompletionResult({
      status: 'PENDING',
      message: 'Usuario bloqueado pelo provedor de liveness. Aguardando a confirmacao final da validacao.',
    });

    this.router.navigateByUrl('/journey-completion');
  }

  private navigateToCompletion(): void {
    const journeyId = this.activeSession?.id || this.awfaceService.getActiveJourneyId();
    if (!journeyId) {
      this.awfaceService.saveCompletionResult({
        status: 'FAILED',
        message: 'A jornada não foi encontrada para confirmar a comunicação final.',
      });
    } else {
      this.awfaceService.saveCompletionResult({
        status: 'PENDING',
        message: 'A prova de vida foi enviada e está em processo de validação.',
      });
    }

    this.router.navigateByUrl('/journey-completion');
  }
}
