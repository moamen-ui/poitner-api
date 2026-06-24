import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import {
  provideHttpClientTesting,
  HttpTestingController,
} from '@angular/common/http/testing';
import { firstValueFrom } from 'rxjs';
import { Api } from './api';

describe('Api', () => {
  let api: Api;
  let controller: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(Api);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    controller.verify();
  });

  it('returns data when envelope isSuccess is true', async () => {
    const promise = firstValueFrom(api.get<{ name: string }>('/api/test'));

    const req = controller.expectOne('http://localhost:8090/api/test');
    req.flush({ isSuccess: true, message: null, data: { name: 'hello' } });

    const data = await promise;
    expect(data).toEqual({ name: 'hello' });
  });

  it('throws an error when envelope isSuccess is false', async () => {
    const promise = firstValueFrom(api.get<{ name: string }>('/api/test'));

    const req = controller.expectOne('http://localhost:8090/api/test');
    req.flush({ isSuccess: false, message: 'Something went wrong', data: null });

    await expect(promise).rejects.toThrow('Something went wrong');
  });
});
