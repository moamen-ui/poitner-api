// Axios mutator for the generated React/Vue clients. orval points each client's
// `override.mutator` at clients/<framework>/mutator.ts; generate-clients.mjs copies
// THIS tracked template there before running orval (clients/ is gitignored, so the
// mutator must be (re)materialized on every run — including in CI).
//
// It unwraps the API's Result<T> envelope: returns `data.data`, throws on failure.
import Axios, { type AxiosRequestConfig, type AxiosError } from 'axios';

export const AXIOS_INSTANCE = Axios.create({ baseURL: '' });

export const customInstance = <T>(config: AxiosRequestConfig): Promise<T> => {
  return AXIOS_INSTANCE({ ...config }).then(({ data }) => {
    if (!data?.isSuccess) throw new Error(data?.message || 'Request failed');
    return data.data as T;
  });
};

export type ErrorType<Error> = AxiosError<Error>;
export type BodyType<BodyData> = BodyData;
