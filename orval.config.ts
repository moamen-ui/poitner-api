import { defineConfig } from 'orval';

const input = {
  target: './openapi.json',
  filters: {
    tags: ['Auth', 'Me', 'Users', 'Stats', 'Projects', 'Roles', 'Statuses'],
  },
};

export default defineConfig({
  angular: {
    input,
    output: {
      mode: 'tags-split',
      target: 'clients/angular/src',
      schemas: 'clients/angular/src/model',
      client: 'angular',
      clean: true,
      formatter: 'prettier',
      override: {
        angular: {
          retrievalClient: 'httpResource',
          provideIn: 'root',
        },
      },
    },
  },
  react: {
    input,
    output: {
      mode: 'tags-split',
      target: 'clients/react/src',
      schemas: 'clients/react/src/model',
      client: 'react-query',
      httpClient: 'axios',
      clean: ['!**/mutator.ts'],
      formatter: 'prettier',
      override: {
        mutator: {
          path: './clients/react/src/mutator.ts',
          name: 'customInstance',
        },
        query: {
          signal: true,
        },
      },
    },
  },
  vue: {
    input,
    output: {
      mode: 'tags-split',
      target: 'clients/vue/src',
      schemas: 'clients/vue/src/model',
      client: 'vue-query',
      httpClient: 'axios',
      clean: ['!**/mutator.ts'],
      formatter: 'prettier',
      override: {
        mutator: {
          path: './clients/vue/src/mutator.ts',
          name: 'customInstance',
        },
        query: {
          signal: true,
        },
      },
    },
  },
});
