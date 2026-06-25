import { defineConfig } from 'orval';

export default defineConfig({
  pointer: {
    input: {
      target: './openapi.json',
      filters: {
        tags: ['Auth', 'Me', 'Users', 'Stats', 'Projects', 'Roles'],
      },
    },
    output: {
      mode: 'tags-split',
      target: 'src/app/core/api/generated',
      schemas: 'src/app/core/api/generated/model',
      client: 'angular',
      clean: true,
      formatter: 'prettier',
      tsconfig: './tsconfig.app.json',
      override: {
        angular: {
          retrievalClient: 'httpResource',
          provideIn: 'root',
        },
      },
    },
  },
});
