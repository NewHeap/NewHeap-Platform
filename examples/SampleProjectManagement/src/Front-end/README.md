# SampleProjectManagement front-ends

This Angular workspace contains two concrete applications:

- `management`: manages projects and demonstrates API integration.
- `workspace`: provides the day-to-day project view with a compact board.

The `projects/sample-project-management-common` directory contains only code
that both applications genuinely share: project models, sample data, and the
API service built on `NhBaseApiService`.

```bash
npm install
npm run start:management
npm run start:workspace
```

The Aspire AppHost starts both applications together with the API.
