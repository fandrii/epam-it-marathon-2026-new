# Restaurant App

Restaurant App is a full-stack, cloud-hosted restaurant management application built during the EPAM IT Marathon 2026. It supports table reservations, individual and group pre-orders, guest participation through invitation links, waiter workflows, menu and location management, feedback, notifications, and operational reports.

## Live demo

[Open the Restaurant App](https://dangje0felyro.cloudfront.net)

The demo is hosted with Amazon CloudFront. Availability depends on the AWS training environment remaining active.

## Features

- Account registration, sign-in, profile management, and role-based access
- Restaurant locations, menus, dishes, tables, and available time slots
- Reservation creation, editing, cancellation, and status tracking
- Individual pre-orders and waiter-assisted order management
- Group reservation invitations and guest carts without requiring guest accounts
- Consolidated group submission with duplicate dish quantities combined
- Waiter notifications and customer feedback
- PDF and spreadsheet report generation and report history
- Responsive Angular interface backed by serverless AWS services

## Group pre-order homework levels

### Level 1 — Group session overview

The reservation owner can retrieve a snapshot of every participant's session, selected dishes, quantities, submission state, participant totals, and the complete group total.

### Level 2 — Owner session and consolidated submission

The application exposes the owner's guest-session ID and lets the owner submit eligible guest carts as one consolidated pre-order. Identical dishes are aggregated, and the related order and group reservation statuses are updated consistently.

### Level 3 — Guest session removal

The owner can remove an eligible guest session from the group. Authorization and group ownership are validated, and the API returns the refreshed participant snapshot and group total.

## Technology stack

### Frontend

- Angular 22 and TypeScript 6
- Angular Material and CDK
- RxJS, SCSS, Jasmine, and Karma

### Backend

- .NET 10 and C#
- AWS Lambda and API Gateway
- Amazon DynamoDB, Cognito, S3, SQS, and EventBridge
- AWS Syndicate for packaging and deployment
- .NET test projects with NUnit and Moq

### Infrastructure and delivery

- Terraform for shared AWS infrastructure
- Amazon S3 and CloudFront for static frontend hosting
- IAM policies and CloudWatch-integrated AWS resources
- npm and Angular CLI for frontend builds

## Repository structure

```text
backend/restaurant-backend-app/     .NET Lambda services, tests, and Syndicate resources
frontend/restaurant-frontend-app/   Angular application and frontend tests
infra/terraform/                    Terraform infrastructure definitions
deployment_guide.md                 Deployment and cleanup instructions
homework-backend.md                 Backend implementation notes
```

## Local development

### Frontend

```bash
cd frontend/restaurant-frontend-app
npm ci
npm start
```

The development server is available at `http://localhost:4200`. The checked-in development environment contains a deployed training API URL; use a local environment override when working against another backend.

### Backend

```bash
cd backend/restaurant-backend-app
dotnet restore Tests/APIHandler.Tests.csproj
dotnet test Tests/APIHandler.Tests.csproj
```

Python virtual environments, Syndicate bundles, .NET build output, Angular build output, Terraform state, credentials, and local environment files are intentionally excluded from version control.

See [deployment_guide.md](deployment_guide.md) for infrastructure and deployment details. Never commit AWS credentials, Terraform variable files containing sensitive values, generated production environment files, or local deployment backups.

## AI-Assisted Development

ChatGPT and Codex AI agents assisted with implementation, code review, automated test development, troubleshooting, and AWS deployment workflows. Their suggestions and generated changes were reviewed, tested, and integrated by the developer; responsibility for the final code and deployment configuration remains with the project maintainer.

## Repository

[fandrii/epam-it-marathon-2026-new](https://github.com/fandrii/epam-it-marathon-2026-new)
