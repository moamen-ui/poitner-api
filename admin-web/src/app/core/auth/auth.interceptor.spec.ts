import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  provideHttpClientTesting,
  HttpTestingController,
} from '@angular/common/http/testing';
import { HttpClient } from '@angular/common/http';
import { apiInterceptor } from './auth.interceptor';
import { provideRouter } from '@angular/router';

const localStorageMock = (() => {
  let store: Record<string, string> = {};
  return {
    getItem: (key: string) => store[key] ?? null,
    setItem: (key: string, value: string) => { store[key] = value; },
    removeItem: (key: string) => { delete store[key]; },
    clear: () => { store = {}; },
  };
})();

Object.defineProperty(globalThis, 'localStorage', {
  value: localStorageMock,
  writable: true,
});

describe('apiInterceptor', () => {
  let http: HttpClient;
  let controller: HttpTestingController;

  beforeEach(async () => {
    localStorageMock.clear();
    await TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        provideHttpClient(withInterceptors([apiInterceptor])),
        provideHttpClientTesting(),
      ],
    }).compileComponents();
    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    controller.verify();
    localStorageMock.clear();
  });

  it('adds Authorization header when token is in localStorage', () => {
    localStorageMock.setItem('pointer_admin_token', 'test-jwt-token');

    http.get('/api/test').subscribe();

    const req = controller.expectOne('http://localhost:8090/api/test');
    expect(req.request.headers.get('Authorization')).toBe('Bearer test-jwt-token');
    req.flush({ isSuccess: true, message: null, data: {} });
  });

  it('does not add Authorization header when no token in localStorage', () => {
    http.get('/api/test').subscribe();

    const req = controller.expectOne('http://localhost:8090/api/test');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush({ isSuccess: true, message: null, data: {} });
  });

  it('unwraps Result envelope and returns data', (done) => {
    http.get<{ name: string }>('/api/test').subscribe({
      next: (data) => {
        expect(data).toEqual({ name: 'hello' });
        done();
      },
      error: done.fail,
    });

    const req = controller.expectOne('http://localhost:8090/api/test');
    req.flush({ isSuccess: true, message: null, data: { name: 'hello' } });
  });

  it('throws when envelope isSuccess is false', (done) => {
    http.get('/api/test').subscribe({
      next: () => done.fail('should have thrown'),
      error: (err) => {
        expect(err.message).toBe('Something went wrong');
        done();
      },
    });

    const req = controller.expectOne('http://localhost:8090/api/test');
    req.flush({ isSuccess: false, message: 'Something went wrong', data: null });
  });
});
