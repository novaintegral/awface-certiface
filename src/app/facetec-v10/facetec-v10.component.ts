import { Component, NgZone, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { FaceTecSDK } from "../../assets/core-sdk-v10/core-sdk/FaceTecSDK.js/FaceTecSDK";
import { Config } from "../../assets/facetec-v10/Config";
import { FaceTecInitializationError, type FaceTecSDKInstance, FaceTecSessionResult } from '../../assets/core-sdk-v10/core-sdk/FaceTecSDK.js/FaceTecPublicApi';
import { SessionRequestProcessor } from '../../assets/facetec-v10/SessionRequestProcessor';
import { SampleAppUtilities } from '../../assets/facetec-v10/utilities/SampleAppUtilities';
import { ThemeHelpers } from 'src/assets/facetec-v10/utilities/ThemeHelpers';
import { DeveloperStatusMessages } from '../../assets/facetec-v10/utilities/DeveloperStatusMessages';
import { Facetecv10UiService } from './facetec-v10-ui.service';
import { AwfaceService } from '../awface/awface.service';
import { AwfaceJourneySession } from '../awface/models';

@Component({
  selector: 'app-facetec-v10',
  templateUrl: './facetec-v10.component.html',
  styleUrls: ['./facetec-v10.component.css']
})
export class FacetecV10Component implements OnInit, OnDestroy {
  FacetecLogo: string = '/assets/img/logo_certiface_trans.png';
  status: string = "";
  appkey: any;
  facetecStrings: any;
  activeSession: AwfaceJourneySession | null = null;
  journeySubjectName = '';
  journeyTypeLabel = '';
  isAutonomousJourney = false;
  isLivenessReady = false;
  livenessButtonLabel = 'Iniciar prova de vida';

  private faceTecSDKInstance!: FaceTecSDKInstance;
  private themeHelpers!: ThemeHelpers;
  private sdkV10: any
  private readonly sessionCompletedHandler = (): void => {
    this.ngZone.run(() => this.navigateToCompletion());
  };
  private readonly sessionRejectedHandler = (): void => {
    this.ngZone.run(() => this.handleRejectedSession());
  };
  private readonly sessionInterruptedHandler = (): void => {
    this.ngZone.run(() => this.prepareInterruptedRetry());
  };

  constructor(
    private router: Router,
    private ngZone: NgZone,
    private facetecv10UiService: Facetecv10UiService,
    public awfaceService: AwfaceService
  ) { }

  async ngOnInit() {
    this.appkey = this.awfaceService.getRuntimeAppkey();
    this.activeSession = this.awfaceService.getActiveSession();
    this.journeySubjectName = this.activeSession?.subject.fullName || this.awfaceService.getActiveJourneySubjectName() || '';
    const journeyType = this.activeSession?.journeyType || this.awfaceService.getActiveJourneyType();
    this.journeyTypeLabel = journeyType ? this.awfaceService.getJourneyLabel(journeyType) : '';
    this.isAutonomousJourney = this.awfaceService.getActiveJourneySource() === 'AUTONOMOUS';

    this.FacetecLogo = this.awfaceService.getJourneyLogoSource(this.activeSession);
    await this.captureDeviceLocation();

    window.addEventListener('awface:liveness-session-completed', this.sessionCompletedHandler);
    window.addEventListener('awface:liveness-session-rejected', this.sessionRejectedHandler);
    window.addEventListener('awface:liveness-session-interrupted', this.sessionInterruptedHandler);

    DeveloperStatusMessages.displayMessage("Preparando a câmera...")

    await this.facetecv10UiService.formatUIForDevice();

    // Ajuste para carregar a localização pt-br
    const module = await import('src/assets/10.0.42/core-sdk-optional/FaceTecStrings.pt-br.js');
    this.facetecStrings = module.default;

    await this.loadFaceTecV10();

    this.initializeFaceTecSDK();
  }

  ngOnDestroy(): void {
    window.removeEventListener('awface:liveness-session-completed', this.sessionCompletedHandler);
    window.removeEventListener('awface:liveness-session-rejected', this.sessionRejectedHandler);
    window.removeEventListener('awface:liveness-session-interrupted', this.sessionInterruptedHandler);
  }

  public showLiveness3D() {
    this.livenessButtonLabel = 'Iniciar prova de vida';
    this.isLivenessReady = false;
    SampleAppUtilities.fadeOutMainUIAndPrepareForSession();

    this.ensureCurrentAppkey().then(() => {
      this.faceTecSDKInstance.start3DLiveness(new SessionRequestProcessor());
    }).catch(error => {
      console.error(error);
      DeveloperStatusMessages.displayMessage('<strong class="awface-neutral-status">Nao foi possivel preparar uma nova tentativa.</strong>');
      SampleAppUtilities.showMainUI();
      this.isLivenessReady = true;
    });
  };

  private async ensureCurrentAppkey(): Promise<void> {
    if (!this.activeSession) {
      return;
    }

    this.appkey = await this.awfaceService.issueAppkey(this.activeSession).toPromise();
  }

  public deleteAppKey() {
    this.awfaceService.clearRuntimeState();

    this.router.navigateByUrl('/journey-start');
  };

  public cancelProcess() {
    this.awfaceService.clearRuntimeState();

    window.close();
  };

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
        {
          enableHighAccuracy: true,
          timeout: 10000,
          maximumAge: 0,
        }
      );
    });
  }

  private initializeFaceTecSDK = (): void => {
    this.sdkV10.setResourceDirectory("../assets/10.0.42/core-sdk/FaceTecSDK.js/resources");
    this.sdkV10.setImagesDirectory("../assets/10.0.42/core-sdk/FaceTec_images");

    this.sdkV10.initializeWithSessionRequest(Config.DeviceKeyIdentifier, new SessionRequestProcessor(),
      {
        onSuccess: (newFaceTecSdkInstance: FaceTecSDKInstance) => {
          this.faceTecSDKInstance = newFaceTecSdkInstance;
          this.onFaceTecSDKInitializationSuccess();
        },
        onError: (initializationError: FaceTecInitializationError) => {
          this.onFaceTecSDKInitializationFailure(initializationError);
        }
      }
    );
  };

  private onFaceTecSDKInitializationSuccess = (): void => {
    this.sdkV10.configureLocalization(this.facetecStrings);
    this.themeHelpers.setAppTheme("Oiti-Dark");

    this.isLivenessReady = true;
    SampleAppUtilities.setupAndFadeInMainUIOnInitializationSuccess();
    DeveloperStatusMessages.logAndDisplayMessage("Inicializado com sucesso");
  };

  private onFaceTecSDKInitializationFailure = (initializationError: FaceTecInitializationError): void => {
    SampleAppUtilities.fadeInMainUIContainer();
    console.log(initializationError);
    switch (initializationError) {
      case 0:
        DeveloperStatusMessages.displayMessage("Servidor da FaceTec não pode validar esta aplicação");
        break;
      case 1:
        DeveloperStatusMessages.displayMessage("Sua appkey é inválida. Por favor, retorne para a home clicando no link no final da tela");
        break;
      case 2:
        DeveloperStatusMessages.displayMessage("Dispositivo não suportado");
        break;
      case 3:
        DeveloperStatusMessages.displayMessage("Ocorreu um erro inesperado");
        break;
      case 4:
        DeveloperStatusMessages.displayMessage("Falha ao carregar recursos na inicialização");
        break;
      case 5:
        DeveloperStatusMessages.displayMessage("APIs de câmera do browser funcionam apenas em localhost ou https");
        break;
      default:
        DeveloperStatusMessages.displayMessage("Erro interno");
        break;
    }
  };

  public static demonstrateHandlingFaceTecExit = (faceTecSessionResult: FaceTecSessionResult): void => {
    DeveloperStatusMessages.logSessionStatusOnFaceTecExit(faceTecSessionResult.status);
    console.log(faceTecSessionResult)

    switch (faceTecSessionResult.status) {
      case FaceTecSDK.FaceTecSessionStatus.RequestAborted:
        window.dispatchEvent(new CustomEvent('awface:liveness-session-rejected', {
          detail: { codId: (window as any).__awfaceLastLivenessCodId }
        }));
        break;
      case FaceTecSDK.FaceTecSessionStatus.SessionCompleted:
        DeveloperStatusMessages.displayMessage('Prova de Vida em processo de validação... <span class="awface-status-spinner"></span>')
        window.dispatchEvent(new CustomEvent('awface:liveness-session-completed'));
        break;
      case FaceTecSDK.FaceTecSessionStatus.UserCancelledFaceScan:
        DeveloperStatusMessages.displayMessage('<strong class="awface-neutral-status">Saiu da tela inteira sem concluir a prova de vida.</strong>')
        window.dispatchEvent(new CustomEvent('awface:liveness-session-interrupted'));
        break;
      case FaceTecSDK.FaceTecSessionStatus.LockedOut:
        DeveloperStatusMessages.displayMessage("O dispositivo está bloqueado do FaceTec Browser SDK");
        break;
      case FaceTecSDK.FaceTecSessionStatus.CameraPermissionsDenied:
        DeveloperStatusMessages.displayMessage("Não há permissão de câmera");
        break;
      case FaceTecSDK.FaceTecSessionStatus.IFrameNotAllowedWithoutPermission:
        DeveloperStatusMessages.displayMessage("FaceTec Browser SDK foi aberto em um IFrame sem permissão");
        break;
      default:
        DeveloperStatusMessages.displayMessage("Erro interno");
        break;
    }
    SampleAppUtilities.showMainUI();
  };

  private handleRejectedSession(): void {
    const codId = Number((window as any).__awfaceLastLivenessCodId);
    (window as any).__awfaceLastLivenessCodId = undefined;

    if (codId === 300.2) {
      this.navigateToBlockedCompletion();
      return;
    }

    this.prepareRetry();
  }

  private navigateToBlockedCompletion(): void {
    this.awfaceService.saveCompletionResult({
      status: 'PENDING',
      message: 'Usu?rio bloqueado pelo provedor de liveness. Aguardando a confirma??o final da valida??o.',
    });

    this.router.navigateByUrl('/journey-completion');
  }

  private prepareRetry(): void {
    this.livenessButtonLabel = 'Tente novamente';
    this.isLivenessReady = true;
    DeveloperStatusMessages.displayMessage('<strong class="awface-neutral-status">Prova de Vida reprovada.</strong>');
    this.enableLivenessButtonAfterSdkTransition();
  }

  private prepareInterruptedRetry(): void {
    this.livenessButtonLabel = 'Iniciar prova de vida';
    this.isLivenessReady = true;
    this.enableLivenessButtonAfterSdkTransition();
  }

  private enableLivenessButtonAfterSdkTransition(): void {
    const enableButton = (): void => {
      this.isLivenessReady = true;
      document.getElementById('liveness-button')?.removeAttribute('disabled');
    };

    enableButton();
    window.setTimeout(enableButton, 900);
  }

  private loadScript(src: string): Promise<void> {
    return new Promise((resolve, reject) => {
      const script = document.createElement('script');
      script.src = src;
      script.async = false;

      script.onload = () => resolve();
      script.onerror = () => reject(`Erro ao carregar ${src}`);

      document.body.appendChild(script);
    });
  }

  private async loadFaceTecV10(): Promise<void> {
    (window as any).FaceTecSDK = undefined;
    await this.loadScript('assets/10.0.42/core-sdk/FaceTecSDK.js/FaceTecSDK.js');
    this.sdkV10 = (window as any).FaceTecSDK;

    if (!this.sdkV10) {
      throw new Error('FaceTec SDK V10 não carregou corretamente');
    }

    this.themeHelpers = new ThemeHelpers(this.sdkV10);
    (window as any).FaceTecSDK = undefined;
  }

  private navigateToCompletion(): void {
    const journeyId = this.activeSession?.id || this.awfaceService.getActiveJourneyId();
    if (!journeyId) {
      this.awfaceService.saveCompletionResult({
        status: 'FAILED',
        message: 'A prova de vida foi concluída, mas a jornada não foi encontrada para confirmar a comunicação final.',
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
