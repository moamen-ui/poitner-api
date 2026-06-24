import { inject, Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { map, Observable } from 'rxjs';
import { environment } from '../../../environments/environment';
import { Envelope } from './models';

@Injectable({ providedIn: 'root' })
export class Api {
  private http = inject(HttpClient);
  private base = environment.apiBase;

  private unwrap<T>(o: Observable<Envelope<T>>): Observable<T> {
    return o.pipe(map((e) => {
      if (!e || e.isSuccess === false) throw new Error((e && e.message) || 'Request failed');
      return e.data;
    }));
  }
  get<T>(path: string): Observable<T> { return this.unwrap(this.http.get<Envelope<T>>(this.base + path)); }
  post<T>(path: string, body: unknown): Observable<T> { return this.unwrap(this.http.post<Envelope<T>>(this.base + path, body)); }
  patch<T>(path: string, body: unknown): Observable<T> { return this.unwrap(this.http.patch<Envelope<T>>(this.base + path, body)); }
}
