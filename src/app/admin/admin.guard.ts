import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { AwfaceService } from '../awface/awface.service';

/** Bloqueia as telas administrativas sem sessão válida (a API também exige o token). */
export const adminGuard: CanActivateFn = () => {
  const awfaceService = inject(AwfaceService);
  return awfaceService.isAdminAuthenticated() ? true : inject(Router).createUrlTree(['/admin']);
};
