import { Injectable } from '@angular/core';
import { HttpErrorResponse, HttpEvent, HttpHandler, HttpInterceptor, HttpRequest } from '@angular/common/http';
import { Router } from '@angular/router';
import { Observable, catchError, throwError } from 'rxjs';
import { AwfaceService } from './awface.service';

export const AWFACE_JOURNEY_TOKEN_HEADER = 'X-AWFace-Journey-Token';

/**
 * Anexa as credenciais exigidas pela AWFace.Api:
 * - Authorization: Bearer (sessão do administrador) nas rotas /admin;
 * - X-AWFace-Journey-Token (sessão da jornada) nas rotas internas da jornada e do SDK.
 */
@Injectable()
export class AwfaceAuthInterceptor implements HttpInterceptor {
  constructor(private awfaceService: AwfaceService, private router: Router) {}

  intercept(request: HttpRequest<unknown>, next: HttpHandler): Observable<HttpEvent<unknown>> {
    const url = request.url;
    const isAdminRequest = url.includes('/api/awface/admin/') && !url.endsWith('/api/awface/admin/login');
    const isJourneyRequest = url.includes('/api/awface/journeys/') || url.includes('/api/awface/facetec/');
    let authorizedRequest = request;

    if (isAdminRequest) {
      const adminToken = this.awfaceService.getAdminToken();
      if (adminToken) {
        authorizedRequest = request.clone({ setHeaders: { Authorization: `Bearer ${adminToken}` } });
      }
    } else if (isJourneyRequest) {
      const journeyToken = this.awfaceService.getJourneyToken();
      if (journeyToken) {
        authorizedRequest = request.clone({ setHeaders: { [AWFACE_JOURNEY_TOKEN_HEADER]: journeyToken } });
      }
    }

    return next.handle(authorizedRequest).pipe(
      catchError((error: HttpErrorResponse) => {
        if (isAdminRequest && error.status === 401) {
          this.awfaceService.logoutAdmin();
          this.router.navigateByUrl('/admin');
        }

        return throwError(() => error);
      })
    );
  }
}
