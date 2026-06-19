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

const ADMIN_SESSION_KEY = 'awface.admin.session';
const ACTIVE_SESSION_KEY = 'awface.activeJourneySessionId';
const ACTIVE_SESSION_SOURCE_KEY = 'awface.activeJourneySource';
const COMPLETION_KEY = 'awface.completion';
const APPKEY_KEY = 'appkey';
const TENANT_LOGO_URL_KEY = 'awface.tenantLogoUrl';
const JOURNEY_SUBJECT_NAME_KEY = 'awface.journeySubjectName';
const JOURNEY_TYPE_KEY = 'awface.journeyType';
const LEGACY_LOCAL_STORAGE_KEYS = [
  'awface.sessions',
  'awface.tenants',
  'awface.deviceLocation',
  'awface.completion',
  'awface.activeJourneySessionId',
  'awface.activeJourneySource',
  'awface.admin.session',
  'hasLiveness',
];

interface AwfaceRuntimeState {
  appkey?: string;
  deviceLocation?: unknown;
}

@Injectable({ providedIn: 'root' })
export class AwfaceService {
  private readonly apiBaseUrl = environment.awfaceApiUrl || '';
  private readonly adminUser = environment.awfaceAdminUser;
  private activeSession: AwfaceJourneySession | null = null;
  private runtimeState: AwfaceRuntimeState = {};

  constructor(private http: HttpClient) {
    this.clearLegacySensitiveLocalStorage();
    (window as any).__awfaceRuntime = this.runtimeState;
  }

  loginAdmin(credentials: AwfaceAdminCredentials): boolean {
    const authenticated =
      credentials.email === this.adminUser.email && credentials.password === this.adminUser.password;

    if (authenticated) {
      sessionStorage.setItem(ADMIN_SESSION_KEY, JSON.stringify({ loggedAt: new Date().toISOString() }));
    }

    return authenticated;
  }

  loginAdminRemote(credentials: AwfaceAdminCredentials): Observable<boolean> {
    return this.http.post<{ authenticated: boolean }>(`${this.apiBaseUrl}/api/awface/admin/login`, credentials).pipe(
      map(response => response.authenticated),
      tap(authenticated => {
        if (authenticated) {
          sessionStorage.setItem(ADMIN_SESSION_KEY, JSON.stringify({ loggedAt: new Date().toISOString() }));
        }
      }),
      catchError(() => of(this.loginAdmin(credentials)))
    );
  }

  logoutAdmin(): void {
    localStorage.removeItem(ADMIN_SESSION_KEY);
    sessionStorage.removeItem(ADMIN_SESSION_KEY);
  }

  isAdminAuthenticated(): boolean {
    return Boolean(sessionStorage.getItem(ADMIN_SESSION_KEY));
  }

  listTenants(): Observable<AwfaceTenant[]> {
    return this.http.get<AwfaceTenant[]>(`${this.apiBaseUrl}/api/awface/admin/tenants`).pipe(
      map(tenants => tenants.map(tenant => this.withTenantDefaults(tenant)))
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
      callbackOAuthEnabled: Boolean(tenant.callbackOAuthEnabled),
      createdAt: tenant.createdAt || now,
      updatedAt: now,
    };

    return this.http.post<AwfaceTenant>(`${this.apiBaseUrl}/api/awface/admin/tenants`, normalized);
  }

  updateTenantStatus(tenantId: string, status: AwfaceTenant['status']): Observable<AwfaceTenant> {
    return this.http.patch<AwfaceTenant>(`${this.apiBaseUrl}/api/awface/admin/tenants/${tenantId}/status`, { status });
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

    this.clearRuntimeState();
    sessionStorage.setItem(ACTIVE_SESSION_SOURCE_KEY, 'ASSISTED');

    return this.http.post<AwfaceJourneySession>(`${this.apiBaseUrl}/api/awface/journeys`, sanitizedRequest).pipe(
      tap(session => this.persistActiveSession(session))
    );
  }

  consumeJourneyLaunch(launchToken: string): Observable<AwfaceJourneySession> {
    this.clearRuntimeState();
    sessionStorage.setItem(ACTIVE_SESSION_SOURCE_KEY, 'AUTONOMOUS');

    return this.http.post<AwfaceJourneyLaunchResolveResponse>(
      `${this.apiBaseUrl}/api/awface/journey-launches/${encodeURIComponent(launchToken)}/consume`,
      {}
    ).pipe(
      map(response => response.session),
      tap(session => this.persistActiveSession(session))
    );
  }

  getActiveSession(): AwfaceJourneySession | null {
    return this.activeSession;
  }

  getActiveJourneySource(): 'ASSISTED' | 'AUTONOMOUS' {
    return sessionStorage.getItem(ACTIVE_SESSION_SOURCE_KEY) === 'AUTONOMOUS' ? 'AUTONOMOUS' : 'ASSISTED';
  }

  getActiveJourneyId(): string | null {
    return sessionStorage.getItem(ACTIVE_SESSION_KEY);
  }

  getActiveJourneySubjectName(): string | null {
    return localStorage.getItem(JOURNEY_SUBJECT_NAME_KEY);
  }

  getActiveJourneyType(): AwfaceJourneyType | null {
    const journeyType = localStorage.getItem(JOURNEY_TYPE_KEY);
    return journeyType && journeyType in AWFACE_JOURNEY_LABELS ? journeyType as AwfaceJourneyType : null;
  }

  registerConsent(sessionId: string, decision: AwfaceConsentDecision): Observable<AwfaceJourneySession> {
    const endpoint = `${this.apiBaseUrl}/api/awface/journeys/${sessionId}/consent`;

    return this.http.post<AwfaceJourneySession>(endpoint, { decision }).pipe(
      tap(session => this.persistActiveSession(session))
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
    sessionStorage.setItem(COMPLETION_KEY, JSON.stringify(result));
  }

  getCompletionResult(): AwfaceCompletionResult | null {
    const value = sessionStorage.getItem(COMPLETION_KEY);
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

  getJourneyLogoSource(session?: AwfaceJourneySession | null, fallback = '/assets/img/logo_certiface_trans.png'): string {
    if (session?.id) {
      return this.createJourneyLogoUrl(session.id);
    }

    return localStorage.getItem(TENANT_LOGO_URL_KEY) || this.getLogoSource(session?.tenant.logoBase64, fallback);
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

  getRuntimeAppkey(): string | undefined {
    return this.runtimeState.appkey || localStorage.getItem(APPKEY_KEY) || undefined;
  }

  getRuntimeDeviceLocation(): unknown {
    return this.runtimeState.deviceLocation;
  }

  setRuntimeDeviceLocation(deviceLocation: unknown): void {
    this.runtimeState.deviceLocation = deviceLocation;
    (window as any).__awfaceRuntime = this.runtimeState;
  }

  clearRuntimeState(): void {
    this.runtimeState = {};
    this.activeSession = null;
    (window as any).__awfaceRuntime = this.runtimeState;
    sessionStorage.removeItem(ACTIVE_SESSION_KEY);
    sessionStorage.removeItem(COMPLETION_KEY);
    localStorage.removeItem(APPKEY_KEY);
    localStorage.removeItem(TENANT_LOGO_URL_KEY);
    localStorage.removeItem(JOURNEY_SUBJECT_NAME_KEY);
    localStorage.removeItem(JOURNEY_TYPE_KEY);
    localStorage.removeItem('hasLiveness');
    localStorage.removeItem('awface.deviceLocation');
  }

  private markSessionAppkey(sessionId: string, appkey: string): void {
    this.runtimeState.appkey = appkey;
    localStorage.setItem(APPKEY_KEY, appkey);
    (window as any).__awfaceRuntime = this.runtimeState;

    if (this.activeSession?.id !== sessionId) {
      return;
    }

    this.activeSession = {
      ...this.activeSession,
      appkey,
      status: 'APPKEY_CREATED',
      updatedAt: new Date().toISOString(),
    };
  }

  private persistActiveSession(session: AwfaceJourneySession): void {
    this.activeSession = session;
    sessionStorage.setItem(ACTIVE_SESSION_KEY, session.id);
    localStorage.setItem(TENANT_LOGO_URL_KEY, this.createJourneyLogoUrl(session.id));
    localStorage.setItem(JOURNEY_SUBJECT_NAME_KEY, session.subject.fullName);
    localStorage.setItem(JOURNEY_TYPE_KEY, session.journeyType);
  }

  private createIntegrationToken(): string {
    return `awf_${crypto.randomUUID().replace(/-/g, '')}`;
  }

  private clearLegacySensitiveLocalStorage(): void {
    LEGACY_LOCAL_STORAGE_KEYS.forEach(key => localStorage.removeItem(key));
  }

  private createJourneyLogoUrl(journeyId: string): string {
    return `${this.apiBaseUrl}/api/awface/journeys/${journeyId}/tenant-logo`;
  }

  private withTenantDefaults(tenant: AwfaceTenant): AwfaceTenant {
    return {
      ...tenant,
      theme: tenant.theme || 'LIGHT',
      primaryColor: this.normalizeColor(tenant.primaryColor, '#007060'),
      secondaryColor: this.normalizeColor(tenant.secondaryColor, '#315f88'),
      callbackOAuthEnabled: Boolean(tenant.callbackOAuthEnabled),
      callbackOAuthTokenUrl: tenant.callbackOAuthTokenUrl || '',
      callbackOAuthClientId: tenant.callbackOAuthClientId || '',
      callbackOAuthClientSecret: tenant.callbackOAuthClientSecret || '',
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
