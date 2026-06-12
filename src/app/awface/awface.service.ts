import { Injectable } from '@angular/core';
import { HttpClient, HttpHeaders, HttpParams } from '@angular/common/http';
import { Observable, catchError, map, of, switchMap, tap, throwError } from 'rxjs';
import { environment } from 'src/environments/environment';
import {
  AWFACE_JOURNEY_LABELS,
  AwfaceAdminCredentials,
  AwfaceConsentDecision,
  AwfaceJourneySession,
  AwfaceJourneyStartRequest,
  AwfaceJourneyType,
  AwfaceTenant,
} from './models';
import { onlyDigits, validateJourneyStart } from './awface-validators';

const TENANTS_KEY = 'awface.tenants';
const SESSIONS_KEY = 'awface.sessions';
const ADMIN_SESSION_KEY = 'awface.admin.session';
const ACTIVE_SESSION_KEY = 'awface.activeJourneySessionId';

@Injectable({ providedIn: 'root' })
export class AwfaceService {
  private readonly apiBaseUrl = environment.awfaceApiUrl || '';
  private readonly certifaceBaseUrl = environment.apiUrl;
  private readonly adminUser = environment.awfaceAdminUser;

  constructor(private http: HttpClient) {
    this.ensureSeedTenant();
  }

  loginAdmin(credentials: AwfaceAdminCredentials): boolean {
    const authenticated =
      credentials.email === this.adminUser.email && credentials.password === this.adminUser.password;

    if (authenticated) {
      localStorage.setItem(ADMIN_SESSION_KEY, JSON.stringify({ email: credentials.email, loggedAt: new Date().toISOString() }));
    }

    return authenticated;
  }

  logoutAdmin(): void {
    localStorage.removeItem(ADMIN_SESSION_KEY);
  }

  isAdminAuthenticated(): boolean {
    return Boolean(localStorage.getItem(ADMIN_SESSION_KEY));
  }

  listTenants(): Observable<AwfaceTenant[]> {
    return this.http.get<AwfaceTenant[]>(`${this.apiBaseUrl}/api/awface/admin/tenants`).pipe(
      catchError(() => of(this.getLocalTenants()))
    );
  }

  saveTenant(tenant: AwfaceTenant): Observable<AwfaceTenant> {
    const now = new Date().toISOString();
    const normalized: AwfaceTenant = {
      ...tenant,
      id: tenant.id || crypto.randomUUID(),
      integrationToken: tenant.integrationToken || this.createIntegrationToken(),
      createdAt: tenant.createdAt || now,
      updatedAt: now,
    };

    return this.http.post<AwfaceTenant>(`${this.apiBaseUrl}/api/awface/admin/tenants`, normalized).pipe(
      catchError(() => {
        const tenants = this.getLocalTenants();
        const nextTenants = tenants.some(item => item.id === normalized.id)
          ? tenants.map(item => (item.id === normalized.id ? normalized : item))
          : [normalized, ...tenants];
        this.setLocalTenants(nextTenants);
        return of(normalized);
      })
    );
  }

  updateTenantStatus(tenantId: string, status: AwfaceTenant['status']): Observable<AwfaceTenant> {
    return this.http.patch<AwfaceTenant>(`${this.apiBaseUrl}/api/awface/admin/tenants/${tenantId}/status`, { status }).pipe(
      catchError(() => {
        const tenants = this.getLocalTenants();
        const tenant = tenants.find(item => item.id === tenantId);
        if (!tenant) {
          return throwError(() => new Error('Tenant não encontrado.'));
        }

        const updated = { ...tenant, status, updatedAt: new Date().toISOString() };
        this.setLocalTenants(tenants.map(item => (item.id === tenantId ? updated : item)));
        return of(updated);
      })
    );
  }

  startJourney(request: AwfaceJourneyStartRequest): Observable<AwfaceJourneySession> {
    const errors = validateJourneyStart(request);
    if (errors.length) {
      return throwError(() => errors);
    }

    const sanitizedRequest = {
      ...request,
      cpf: onlyDigits(request.cpf),
      fullName: request.fullName.trim().replace(/\s+/g, ' '),
      externalClientId: request.externalClientId.trim(),
    };

    return this.http.post<AwfaceJourneySession>(`${this.apiBaseUrl}/api/awface/journeys`, sanitizedRequest).pipe(
      tap(session => this.persistActiveSession(session)),
      catchError(() => {
        const tenant = this.findTenantByToken(sanitizedRequest.integrationToken);

        if (!tenant) {
          return throwError(() => [{ field: 'integrationToken', message: 'Token de integração não encontrado.' }]);
        }

        if (tenant.status !== 'ACTIVE') {
          return throwError(() => [{ field: 'integrationToken', message: 'Tenant bloqueado ou cancelado.' }]);
        }

        if (!tenant.credentials.some(credential => credential.journeyType === sanitizedRequest.journeyType)) {
          return throwError(() => [{ field: 'journeyType', message: 'Tipo de jornada não habilitado para este tenant.' }]);
        }

        const now = new Date().toISOString();
        const session: AwfaceJourneySession = {
          id: crypto.randomUUID(),
          tenant,
          journeyType: sanitizedRequest.journeyType,
          subject: {
            cpf: sanitizedRequest.cpf,
            fullName: sanitizedRequest.fullName,
            birthDate: sanitizedRequest.birthDate,
            externalClientId: sanitizedRequest.externalClientId,
          },
          status: 'CREATED',
          createdAt: now,
          updatedAt: now,
        };

        this.saveLocalSession(session);
        this.persistActiveSession(session);
        return of(session);
      })
    );
  }

  getActiveSession(): AwfaceJourneySession | null {
    const sessionId = localStorage.getItem(ACTIVE_SESSION_KEY);
    if (!sessionId) {
      return null;
    }

    return this.getLocalSessions().find(session => session.id === sessionId) || null;
  }

  registerConsent(sessionId: string, decision: AwfaceConsentDecision): Observable<AwfaceJourneySession> {
    const endpoint = `${this.apiBaseUrl}/api/awface/journeys/${sessionId}/consent`;

    return this.http.post<AwfaceJourneySession>(endpoint, { decision }).pipe(
      tap(session => this.persistActiveSession(session)),
      catchError(() => {
        const session = this.getLocalSessions().find(item => item.id === sessionId);
        if (!session) {
          return throwError(() => new Error('Jornada não encontrada.'));
        }

        const now = new Date().toISOString();
        const updated: AwfaceJourneySession = {
          ...session,
          status: decision === 'ACCEPTED' ? 'CONSENT_ACCEPTED' : 'CONSENT_REFUSED',
          consentAt: decision === 'ACCEPTED' ? now : session.consentAt,
          refusalAt: decision === 'REFUSED' ? now : session.refusalAt,
          updatedAt: now,
        };
        this.saveLocalSession(updated);
        this.persistActiveSession(updated);
        return of(updated);
      })
    );
  }

  issueAppkey(session: AwfaceJourneySession): Observable<string> {
    const endpoint = `${this.apiBaseUrl}/api/awface/journeys/${session.id}/appkey`;

    return this.http.post<{ appkey: string }>(endpoint, {}).pipe(
      map(response => response.appkey),
      tap(appkey => this.markSessionAppkey(session.id, appkey)),
      catchError(() => this.issueAppkeyDirectly(session))
    );
  }

  getJourneyLabel(journeyType: AwfaceJourneyType): string {
    return AWFACE_JOURNEY_LABELS[journeyType];
  }

  private issueAppkeyDirectly(session: AwfaceJourneySession): Observable<string> {
    const credential = session.tenant.credentials.find(item => item.journeyType === session.journeyType);

    if (!credential) {
      return throwError(() => new Error('Credencial Certiface não configurada para esta jornada.'));
    }

    const credentialBody = new HttpParams()
      .set('user', credential.providerUser)
      .set('pass', credential.providerPass);

    const headers = new HttpHeaders({ 'Content-Type': 'application/x-www-form-urlencoded' });

    return this.http
      .post<{ token: string; expires: string }>(
        `${this.certifaceBaseUrl}/facecaptcha/service/captcha/credencial`,
        credentialBody.toString(),
        { headers }
      )
      .pipe(
        map(token => ({ token, credential })),
        map(({ token, credential }) => {
          return new HttpParams()
            .set('user', credential.providerUser)
            .set('token', JSON.stringify(token))
            .set('cpf', session.subject.cpf)
            .set('nome', session.subject.fullName)
            .set('nascimento', this.toBrazilianDate(session.subject.birthDate))
            .set('idExternoCliente', session.subject.externalClientId);
        }),
        switchMap(body => this.postAppkey(session.id, body, headers))
      );
  }

  private postAppkey(sessionId: string, body: HttpParams, headers: HttpHeaders): Observable<string> {
    return this.http
      .post<{ appkey: string }>(
        `${this.certifaceBaseUrl}/facecaptcha/service/captcha/appkey`,
        body.toString(),
        { headers }
      )
      .pipe(
        map(response => response.appkey),
        tap(appkey => this.markSessionAppkey(sessionId, appkey))
      );
  }

  private markSessionAppkey(sessionId: string, appkey: string): void {
    const session = this.getLocalSessions().find(item => item.id === sessionId);
    if (!session) {
      return;
    }

    this.saveLocalSession({
      ...session,
      appkey,
      status: 'APPKEY_CREATED',
      updatedAt: new Date().toISOString(),
    });
    localStorage.setItem('appkey', appkey);
  }

  private persistActiveSession(session: AwfaceJourneySession): void {
    localStorage.setItem(ACTIVE_SESSION_KEY, session.id);
  }

  private saveLocalSession(session: AwfaceJourneySession): void {
    const sessions = this.getLocalSessions();
    const nextSessions = sessions.some(item => item.id === session.id)
      ? sessions.map(item => (item.id === session.id ? session : item))
      : [session, ...sessions];

    localStorage.setItem(SESSIONS_KEY, JSON.stringify(nextSessions));
  }

  private getLocalSessions(): AwfaceJourneySession[] {
    return JSON.parse(localStorage.getItem(SESSIONS_KEY) || '[]');
  }

  private findTenantByToken(integrationToken: string): AwfaceTenant | undefined {
    return this.getLocalTenants().find(tenant => tenant.integrationToken === integrationToken);
  }

  private getLocalTenants(): AwfaceTenant[] {
    return JSON.parse(localStorage.getItem(TENANTS_KEY) || '[]');
  }

  private setLocalTenants(tenants: AwfaceTenant[]): void {
    localStorage.setItem(TENANTS_KEY, JSON.stringify(tenants));
  }

  private createIntegrationToken(): string {
    return `awf_${crypto.randomUUID().replace(/-/g, '')}`;
  }

  private ensureSeedTenant(): void {
    if (this.getLocalTenants().length) {
      return;
    }

    const now = new Date().toISOString();
    this.setLocalTenants([
      {
        id: 'tenant-demo',
        name: 'Tenant Demonstração',
        integrationToken: 'awf_demo_token',
        status: 'ACTIVE',
        termsUrl: 'https://awface.com.br/termos',
        privacyUrl: 'https://awface.com.br/privacidade',
        callbackUrl: 'https://host.example.com/webhook/awface',
        secureCallbackToken: 'demo-secure-callback-token',
        credentials: [
          {
            journeyType: 'LIVENESS',
            providerUser: 'login',
            providerPass: '3355a3973f54a008c642ee94fd0313d7',
          },
        ],
        createdAt: now,
        updatedAt: now,
      },
    ]);
  }

  private toBrazilianDate(value: string): string {
    const [year, month, day] = value.split('-');
    return `${day}/${month}/${year}`;
  }
}
