import { environment } from 'src/environments/environment';
import { Crypto } from '../utilities/Crypto';
import {
  FaceTecFaceScanProcessor,
  FaceTecFaceScanResultCallback,
  FaceTecSessionResult,
} from '../9.7.109/core-sdk/FaceTecSDK.js/FaceTecPublicApi';

export interface FacetecV9ProcessorEvents {
  completed(): void;
  rejected(): void;
  blocked(): void;
  interrupted(): void;
  failed(message: string): void;
}

export class FacetecV9Processor implements FaceTecFaceScanProcessor {
  private request = new XMLHttpRequest();
  private completedSuccessfully = false;

  constructor(
    sessionToken: string,
    private readonly sdk: any,
    private readonly appkey: string,
    private readonly deviceLocation: unknown,
    private readonly events: FacetecV9ProcessorEvents
  ) {
    new this.sdk.FaceTecSession(this, sessionToken);
  }

  processSessionResultWhileFaceTecSDKWaits = (
    sessionResult: FaceTecSessionResult,
    callback: FaceTecFaceScanResultCallback
  ): void => {
    if (sessionResult.status !== this.sdk.FaceTecSessionStatus.SessionCompletedSuccessfully) {
      callback.cancel();
      this.events.interrupted();
      return;
    }

    const payload = {
      appkey: this.appkey,
      userAgent: this.sdk.createFaceTecAPIUserAgentString(sessionResult.sessionId || ''),
      faceScan: sessionResult.faceScan,
      auditTrailImage: Crypto.encryptImages(sessionResult.auditTrail[0] || '', this.appkey),
      lowQualityAuditTrailImage: Crypto.encryptImages(sessionResult.lowQualityAuditTrail[0] || '', this.appkey),
      sessionId: sessionResult.sessionId,
      deviceLocation: this.deviceLocation || undefined,
    };

    this.request.open(
      'POST',
      `${environment.awfaceApiUrl || ''}/api/awface/facetec/v9/3d/liveness`
    );
    this.request.setRequestHeader('Content-Type', 'application/json');

    this.request.onreadystatechange = (): void => {
      if (this.request.readyState !== XMLHttpRequest.DONE) {
        return;
      }

      if (this.request.status < 200 || this.request.status >= 300) {
        callback.cancel();
        this.events.failed('Não foi possível validar a prova de vida.');
        return;
      }

      try {
        const response = JSON.parse(this.request.responseText);
        const codId = Number(response.codID);

        if (codId === 300.1) {
          callback.cancel();
          this.events.rejected();
          return;
        }

        if (codId === 300.2) {
          callback.cancel();
          this.events.blocked();
          return;
        }

        if (codId < 200 || codId >= 300 || !response.scanResultBlob) {
          callback.cancel();
          this.events.failed('A Certiface retornou uma resposta de liveness não reconhecida.');
          return;
        }

        this.completedSuccessfully = true;
        this.sdk.FaceTecCustomization.setOverrideResultScreenSuccessMessage('Liveness\nConfirmado');
        callback.proceedToNextStep(response.scanResultBlob);
      } catch {
        callback.cancel();
        this.events.failed('Não foi possível interpretar o resultado da prova de vida.');
      }
    };

    this.request.onerror = (): void => {
      callback.cancel();
      this.events.failed('Falha de comunicação durante a prova de vida.');
    };

    this.request.upload.onprogress = (event: ProgressEvent): void => {
      if (event.total > 0) {
        callback.uploadProgress(event.loaded / event.total);
      }
    };

    this.request.send(JSON.stringify(payload));

    window.setTimeout(() => {
      if (this.request.readyState !== XMLHttpRequest.DONE) {
        callback.uploadMessageOverride('Ainda enviando...');
      }
    }, 6000);
  };

  onFaceTecSDKCompletelyDone = (): void => {
    if (this.completedSuccessfully) {
      this.events.completed();
    }
  };
}
