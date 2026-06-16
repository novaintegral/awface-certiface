import { Component, NgZone, OnDestroy, OnInit } from '@angular/core';
import { Router } from '@angular/router';
import { Subscription, switchMap, timer } from 'rxjs';
import { FaceTecSDK } from "../../assets/core-sdk-v10/core-sdk/FaceTecSDK.js/FaceTecSDK";
import { Config } from "../../assets/facetec-v10/Config";
import { FaceTecInitializationError, type FaceTecSDKInstance, FaceTecSessionResult } from '../../assets/core-sdk-v10/core-sdk/FaceTecSDK.js/FaceTecPublicApi';
import { SessionRequestProcessor } from '../../assets/facetec-v10/SessionRequestProcessor';
import { SampleAppUtilities } from '../../assets/facetec-v10/utilities/SampleAppUtilities';
import { ThemeHelpers } from 'src/assets/facetec-v10/utilities/ThemeHelpers';
import { DeveloperStatusMessages } from '../../assets/facetec-v10/utilities/DeveloperStatusMessages';
import { Facetecv10UiService } from './facetec-v10-ui.service';
import { AwfaceService } from '../awface/awface.service';
import { AwfaceCompletionResult, AwfaceJourneySession } from '../awface/models';

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
  isAutonomousJourney = false;

  private faceTecSDKInstance!: FaceTecSDKInstance;
  private themeHelpers!: ThemeHelpers;
  private sdkV10: any
  private completionPolling?: Subscription;
  private readonly sessionCompletedHandler = (): void => {
    this.ngZone.run(() => this.startCompletionPolling());
  };

  constructor(
    private router: Router,
    private ngZone: NgZone,
    private facetecv10UiService: Facetecv10UiService,
    public awfaceService: AwfaceService
  ) { }

  async ngOnInit() {
    this.appkey = window.localStorage.getItem('appkey');
    this.activeSession = this.awfaceService.getActiveSession();
    this.isAutonomousJourney = this.awfaceService.getActiveJourneySource() === 'AUTONOMOUS';

    this.FacetecLogo = this.awfaceService.getLogoSource(this.activeSession?.tenant.logoBase64);

    window.addEventListener('awface:liveness-session-completed', this.sessionCompletedHandler);

    DeveloperStatusMessages.displayMessage("Preparando a câmera...")

    await this.facetecv10UiService.formatUIForDevice();

    // Ajuste para carregar a localização pt-br
    const module = await import('src/assets/core-sdk-v10/core-sdk-optional/FaceTecStrings.pt-br.js');
    this.facetecStrings = module.default;

    await this.loadFaceTecV10();

    this.initializeFaceTecSDK();
  }

  ngOnDestroy(): void {
    window.removeEventListener('awface:liveness-session-completed', this.sessionCompletedHandler);
    this.completionPolling?.unsubscribe();
  }

  public showLiveness3D() {
    SampleAppUtilities.fadeOutMainUIAndPrepareForSession();
    this.faceTecSDKInstance.start3DLiveness(new SessionRequestProcessor());
  };

  public deleteAppKey() {
    window.localStorage.removeItem('appkey');
    window.localStorage.removeItem('hasLiveness');
    window.localStorage.removeItem('awface.completion');

    this.router.navigateByUrl('/journey-start');
  };

  public cancelProcess() {
    window.localStorage.removeItem('appkey');
    window.localStorage.removeItem('hasLiveness');
    window.localStorage.removeItem('awface.completion');

    window.close();
  };

  private initializeFaceTecSDK = (): void => {
    this.sdkV10.setResourceDirectory("../assets/core-sdk-v10/core-sdk/FaceTecSDK.js/resources");
    this.sdkV10.setImagesDirectory("../assets/core-sdk-v10/core-sdk/FaceTec_images");

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
        DeveloperStatusMessages.displayMessage("Prova de Vida reprovada. Insira uma nova appkey e tente novamente");
        break;
      case FaceTecSDK.FaceTecSessionStatus.SessionCompleted:
        DeveloperStatusMessages.displayMessage('Prova de Vida em processo de validação... <span class="awface-status-spinner"></span>')
        window.dispatchEvent(new CustomEvent('awface:liveness-session-completed'));
        break;
      case FaceTecSDK.FaceTecSessionStatus.UserCancelledFaceScan:
        DeveloperStatusMessages.displayMessage("Saiu da tela inteira sem concluir a prova de vida")
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
    await this.loadScript('assets/core-sdk-v10/core-sdk/FaceTecSDK.js/FaceTecSDK.js');
    this.sdkV10 = (window as any).FaceTecSDK;

    if (!this.sdkV10) {
      throw new Error('FaceTec SDK V10 não carregou corretamente');
    }

    this.themeHelpers = new ThemeHelpers(this.sdkV10);
    (window as any).FaceTecSDK = undefined;
  }

  private startCompletionPolling(): void {
    if (!this.activeSession) {
      this.saveAndNavigateToCompletion({
        status: 'FAILED',
        message: 'A prova de vida foi concluída, mas a jornada não foi encontrada para confirmar a comunicação final.',
      });
      return;
    }

    this.completionPolling?.unsubscribe();
    let attempts = 0;

    this.completionPolling = timer(0, 1000).pipe(
      switchMap(() => this.awfaceService.getCompletionStatus(this.activeSession!.id))
    ).subscribe({
      next: result => {
        attempts += 1;
        if (result.status === 'PENDING' && attempts < 60) {
          return;
        }

        if (result.status === 'PENDING') {
          this.saveAndNavigateToCompletion({
            status: 'FAILED',
            message: 'A prova de vida foi concluída, mas a comunicação final demorou mais que o esperado. Entre em contato com o administrador do sistema.',
          });
          return;
        }

        this.saveAndNavigateToCompletion(result);
      },
      error: () => {
        this.saveAndNavigateToCompletion({
          status: 'FAILED',
          message: 'A prova de vida foi concluída, mas não foi possível consultar a comunicação final. Entre em contato com o administrador do sistema.',
        });
      },
    });
  }

  private saveAndNavigateToCompletion(result: AwfaceCompletionResult): void {
    this.completionPolling?.unsubscribe();
    this.awfaceService.saveCompletionResult(result);
    this.router.navigateByUrl('/journey-completion');
  }
}
