import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';

export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const token = localStorage.getItem('pointer_admin_token');
  const authed = token ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  const router = inject(Router);
  return next(authed).pipe(
    catchError((err) => {
      if (err?.status === 401) {
        localStorage.removeItem('pointer_admin_token');
        localStorage.removeItem('pointer_admin_user');
        router.navigateByUrl('/login');
      }
      return throwError(() => err);
    })
  );
};
