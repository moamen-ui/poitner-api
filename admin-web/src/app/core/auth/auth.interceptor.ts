import { HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, map, throwError } from 'rxjs';
import { environment } from '../../../environments/environment';

export const apiInterceptor: HttpInterceptorFn = (req, next) => {
  const isApiRequest = req.url.startsWith('/api/');

  let modifiedReq = req;
  if (isApiRequest) {
    modifiedReq = req.clone({ url: environment.apiBase + req.url });
  }

  const token = localStorage.getItem('pointer_admin_token');
  if (token) {
    modifiedReq = modifiedReq.clone({
      setHeaders: { Authorization: `Bearer ${token}` },
    });
  }

  const router = inject(Router);
  return next(modifiedReq).pipe(
    map((event) => {
      if (event instanceof HttpResponse && isApiRequest) {
        const body = event.body as { isSuccess?: boolean; message?: string | null; data?: unknown } | null;
        if (body && typeof body === 'object' && 'isSuccess' in body) {
          if (!body.isSuccess) {
            throw new Error(body.message || 'Request failed');
          }
          return event.clone({ body: body.data });
        }
      }
      return event;
    }),
    catchError((err) => {
      if (err?.status === 401) {
        localStorage.removeItem('pointer_admin_token');
        localStorage.removeItem('pointer_admin_user');
        router.navigateByUrl('/login');
      }
      return throwError(() => err);
    }),
  );
};
