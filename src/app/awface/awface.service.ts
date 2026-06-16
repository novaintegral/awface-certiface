import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, tap, throwError } from 'rxjs';
import { environment } from 'src/environments/environment';
import {
  AWFACE_JOURNEY_LABELS,
  AwfaceAdminCredentials,
  AwfaceConsentDecision,
  AwfaceCompletionResult,
  AwfaceJourneyLaunchResolveResponse,
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
const COMPLETION_KEY = 'awface.completion';

@Injectable({ providedIn: 'root' })
export class AwfaceService {
  private readonly apiBaseUrl = environment.awfaceApiUrl || '';
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

  loginAdminRemote(credentials: AwfaceAdminCredentials): Observable<boolean> {
    return this.http.post<{ authenticated: boolean }>(`${this.apiBaseUrl}/api/awface/admin/login`, credentials).pipe(
      map(response => response.authenticated),
      tap(authenticated => {
        if (authenticated) {
          localStorage.setItem(ADMIN_SESSION_KEY, JSON.stringify({ email: credentials.email, loggedAt: new Date().toISOString() }));
        }
      }),
      catchError(() => of(this.loginAdmin(credentials)))
    );
  }

  logoutAdmin(): void {
    localStorage.removeItem(ADMIN_SESSION_KEY);
  }

  isAdminAuthenticated(): boolean {
    return Boolean(localStorage.getItem(ADMIN_SESSION_KEY));
  }

  listTenants(): Observable<AwfaceTenant[]> {
    return this.http.get<AwfaceTenant[]>(`${this.apiBaseUrl}/api/awface/admin/tenants`).pipe(
      map(tenants => tenants.map(tenant => this.withTenantDefaults(tenant))),
      catchError(() => of(this.getLocalTenants()))
    );
  }

  saveTenant(tenant: AwfaceTenant): Observable<AwfaceTenant> {
    const now = new Date().toISOString();
    const normalized: AwfaceTenant = {
      ...tenant,
      id: tenant.id || crypto.randomUUID(),
      integrationToken: tenant.integrationToken || this.createIntegrationToken(),
      theme: tenant.theme || 'LIGHT',
      primaryColor: this.normalizeColor(tenant.primaryColor, '#007060'),
      secondaryColor: this.normalizeColor(tenant.secondaryColor, '#315f88'),
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

    localStorage.removeItem('appkey');
    localStorage.removeItem('awface.completion');

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

  consumeJourneyLaunch(launchToken: string): Observable<AwfaceJourneySession> {
    localStorage.removeItem('appkey');
    localStorage.removeItem(COMPLETION_KEY);

    return this.http.post<AwfaceJourneyLaunchResolveResponse>(
      `${this.apiBaseUrl}/api/awface/journey-launches/${encodeURIComponent(launchToken)}/consume`,
      {}
    ).pipe(
      map(response => response.session),
      tap(session => this.persistActiveSession(session))
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
      catchError(error => throwError(() => error))
    );
  }

  getCompletionStatus(sessionId: string): Observable<AwfaceCompletionResult> {
    return this.http.get<AwfaceCompletionResult>(`${this.apiBaseUrl}/api/awface/journeys/${sessionId}/completion`);
  }

  saveCompletionResult(result: AwfaceCompletionResult): void {
    localStorage.setItem(COMPLETION_KEY, JSON.stringify(result));
  }

  getCompletionResult(): AwfaceCompletionResult | null {
    const value = localStorage.getItem(COMPLETION_KEY);
    return value ? JSON.parse(value) : null;
  }

  getJourneyLabel(journeyType: AwfaceJourneyType): string {
    return AWFACE_JOURNEY_LABELS[journeyType];
  }

  getLogoSource(value?: string | null, fallback = '/assets/img/logo_certiface_trans.png'): string {
    const logo = value?.trim();
    if (!logo) {
      return fallback;
    }

    if (/^(data:image\/|https?:\/\/|\/assets\/)/i.test(logo)) {
      return logo;
    }

    return `data:image/png;base64,${logo.replace(/\s/g, '')}`;
  }

  getTenantThemeStyle(tenant?: AwfaceTenant | null): Record<string, string> {
    const primary = this.normalizeColor(tenant?.primaryColor, '#007060');
    const secondary = this.normalizeColor(tenant?.secondaryColor, '#315f88');
    const dark = tenant?.theme === 'DARK';

    return {
      '--awface-primary': primary,
      '--awface-secondary': secondary,
      '--awface-page-bg': dark ? '#0f172a' : '#ffffff',
      '--awface-panel-bg': dark ? '#111827' : '#ffffff',
      '--awface-heading': dark ? '#f8fafc' : '#07152f',
      '--awface-text': dark ? '#dbe4ee' : '#51606f',
      '--awface-muted': dark ? '#94a3b8' : '#5b6777',
      '--awface-border': dark ? '#263449' : '#e7edf3',
      '--awface-soft-primary': this.hexToRgba(primary, dark ? 0.18 : 0.12),
    };
  }

  private markSessionAppkey(sessionId: string, appkey: string): void {
    localStorage.setItem('appkey', appkey);

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
  }

  private persistActiveSession(session: AwfaceJourneySession): void {
    this.saveLocalSession(session);
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
    const tenant = this.getLocalTenants().find(item => item.integrationToken === integrationToken);
    return tenant ? this.withTenantDefaults(tenant) : undefined;
  }

  private getLocalTenants(): AwfaceTenant[] {
    return (JSON.parse(localStorage.getItem(TENANTS_KEY) || '[]') as AwfaceTenant[])
      .map(tenant => this.withTenantDefaults(tenant));
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
        theme: 'LIGHT',
        primaryColor: '#007060',
        secondaryColor: '#315f88',
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

  private withTenantDefaults(tenant: AwfaceTenant): AwfaceTenant {
    return {
      ...tenant,
      theme: tenant.theme || 'LIGHT',
      primaryColor: this.normalizeColor(tenant.primaryColor, '#007060'),
      secondaryColor: this.normalizeColor(tenant.secondaryColor, '#315f88'),
    };
  }

  private normalizeColor(value: string | undefined, fallback: string): string {
    return value?.trim() || fallback;
  }

  private hexToRgba(color: string, alpha: number): string {
    const match = /^#?([a-f\d]{2})([a-f\d]{2})([a-f\d]{2})$/i.exec(color.trim());
    if (!match) {
      return `rgba(0, 112, 96, ${alpha})`;
    }

    const [, r, g, b] = match;
    return `rgba(${parseInt(r, 16)}, ${parseInt(g, 16)}, ${parseInt(b, 16)}, ${alpha})`;
  }

}
