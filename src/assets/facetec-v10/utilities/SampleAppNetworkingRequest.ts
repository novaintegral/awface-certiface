import { environment } from "src/environments/environment";
import { FaceTecSessionRequestProcessorCallback } from "../../core-sdk-v10/core-sdk/FaceTecSDK.js/FaceTecPublicApi";
import { SessionRequestProcessor } from "../SessionRequestProcessor";
import { DeveloperStatusMessages } from "./DeveloperStatusMessages";

export class SampleAppNetworkingRequest {
  private static readonly maxTransientRetries = 2;
  private static readonly transientRetryDelayMs = 750;

  public static send = (
    referencingProcessor: SessionRequestProcessor,
    sessionRequestBlob: string,
    sessionRequestCallback: FaceTecSessionRequestProcessorCallback
  ): void => {

    const runtime = (window as any).__awfaceRuntime || {};
    const appkey = runtime.appkey || window.localStorage.getItem('appkey');
    const userAgent = window.navigator.userAgent;

    if (!appkey) {
      DeveloperStatusMessages.logAndDisplayMessage("Appkey nao encontrada. Recomece a prova de vida.");
      referencingProcessor.onCatastrophicNetworkError(sessionRequestCallback);
      return;
    }

    const deviceLocation = this.getDeviceLocation();
    const sessionRequestCallPayload: { requestBlob: string, appkey: any, userAgent: string, deviceLocation?: any } = {
      requestBlob: sessionRequestBlob,
      appkey: appkey,
      userAgent: userAgent
    };

    if (deviceLocation) {
      sessionRequestCallPayload.deviceLocation = deviceLocation;
    }

    const openAndSendRequest = (attempt: number): any => {
      if (!environment.awfaceApiUrl) {
        throw new Error("AWFace API URL nao configurada para process-request.");
      }

      const processRequestUrl = `${environment.awfaceApiUrl}/api/awface/facetec/v10/3d/process-request`;
      const request: XMLHttpRequest = new XMLHttpRequest();

      request.open("POST", processRequestUrl);
      request.setRequestHeader("Content-Type", "application/json");

      request.onload = (response: any): any => {
        const responseJSON = this.parseResponse(response.target.response);
        console.log(responseJSON)

        const responseBlob: string | null = this.getResponseBlobOrHandleError(request);

        if (responseBlob !== null) {
          DeveloperStatusMessages.validateLivenessResult(responseJSON, sessionRequestCallback);
          referencingProcessor.onResponseBlobReceived(responseBlob, sessionRequestCallback);
          return;
        }

        if (this.shouldRetryTransientError(request.status, attempt)) {
          DeveloperStatusMessages.logMessage(`SampleAppNetworkingRequest >> request.onload >> Retrying transient status ${request.status}. Attempt ${attempt + 1}`);
          window.setTimeout(() => openAndSendRequest(attempt + 1), this.transientRetryDelayMs);
          return;
        }

        referencingProcessor.onCatastrophicNetworkError(sessionRequestCallback);
      };

      request.onerror = (ev: ProgressEvent): void => {
        DeveloperStatusMessages.logMessage(`SampleAppNetworkingRequest >> request.onerror >> Catastrophic error: ${ev}`);

        if (this.shouldRetryTransientError(request.status, attempt)) {
          window.setTimeout(() => openAndSendRequest(attempt + 1), this.transientRetryDelayMs);
          return;
        }

        referencingProcessor.onCatastrophicNetworkError(sessionRequestCallback);
      };

      request.upload.onprogress = (ev: ProgressEvent): void => {
        referencingProcessor.onUploadProgress(ev.loaded / ev.total, sessionRequestCallback);
      };

      request.send(JSON.stringify(sessionRequestCallPayload));
    };

    openAndSendRequest(0);
  };

  private static parseResponse = (responseText: string): any => {
    try {
      return JSON.parse(responseText);
    }
    catch (e) {
      DeveloperStatusMessages.logMessage(`SampleAppNetworkingRequest >> request.onload >> Non-JSON response: ${responseText}`);
      return { error: responseText || "EMPTY_RESPONSE" };
    }
  };

  private static getResponseBlobOrHandleError = (request: XMLHttpRequest): string | null => {
    if (request.status === 200) {
      try {
        const parsedResponse: { responseBlob: string, result?: { [key: string]: string | undefined } } = JSON.parse(request.responseText);

        return parsedResponse.responseBlob;
      }
      catch (e) {
        DeveloperStatusMessages.logMessage(`SampleAppNetworkingRequest >> request.onload >> Failed to parse responseText: ${e}`);
      }
    }
    else {
      DeveloperStatusMessages.logMessage(`SampleAppNetworkingRequest >> request.onload >> Server Status: ${request.status}`);
    }

    return null;
  };

  private static shouldRetryTransientError = (status: number, attempt: number): boolean => {
    return attempt < this.maxTransientRetries && status >= 500 && status < 600;
  };

  private static getDeviceLocation = (): any | null => {
    return ((window as any).__awfaceRuntime || {}).deviceLocation || null;
  };
}
