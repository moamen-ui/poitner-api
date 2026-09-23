import { defineConfig } from 'orval';

const input = {
  target: './openapi.json',
  filters: {
    tags: ['Auth', 'Me', 'Users', 'Stats', 'Projects', 'Roles', 'Statuses', 'Tenants', 'Settings', 'Demo', 'PredefinedActions', 'ExportImport', 'Invites', 'Suggestions', 'Plans', 'Extension', 'Branding', 'AppEnvironments', 'AiRules', 'Meta', 'Events', 'Builds', 'Comments', 'Workspace', 'Audit', 'Identities'],
  },
};

export default defineConfig({
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
});
