import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { environment } from 'src/environments/environment';
import { Crypto } from 'src/assets/utilities/Crypto';

@Injectable({ providedIn: 'root' })
export class FacetecV9Service {
  private readonly apiBaseUrl = environment.awfaceApiUrl || '';

  constructor(private http: HttpClient) {}

  async getProductionKey(appkey: string): Promise<string> {
    const response = await this.postText('/api/awface/facetec/v9/3d/initialize', {
      appkey,
      platform: 'web',
    });
    const payload = this.decodeProviderPayload(response, appkey);

    if (!payload?.productionKey) {
      throw new Error('A Certiface não retornou a chave de produção FaceTec V9.');
    }

    return payload.productionKey;
  }

  async getSessionToken(appkey: string, userAgent: string): Promise<string> {
    const response = await this.postText('/api/awface/facetec/v9/3d/session-token', {
      appkey,
      userAgent,
    });
    const payload = this.decodeProviderPayload(response, appkey);

    if (payload?.vendor === 'IPROOV') {
      throw new Error('A appkey informada pertence a outro provedor de liveness.');
    }

    if (!payload?.sessionToken) {
      throw new Error('A Certiface não retornou o token de sessão FaceTec V9.');
    }

    return payload.sessionToken;
  }

  private postText(path: string, body: object): Promise<string> {
    return firstValueFrom(this.http.post(
      `${this.apiBaseUrl}${path}`,
      body,
      { responseType: 'text' }
    ));
  }

  private decodeProviderPayload(rawResponse: string, appkey: string): any {
    let value = rawResponse.trim();

    try {
      const parsed = JSON.parse(value);
      if (typeof parsed !== 'string') {
        return parsed;
      }
      value = parsed;
    } catch {
      // Respostas V9 criptografadas não são JSON antes da descriptografia.
    }

    const decrypted = Crypto.decChData(value, appkey);
    return JSON.parse(decrypted);
  }
}
