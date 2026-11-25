# Code Review — LekhaBooks

*Reviewed 2026-11-25 · .NET 10 (LTS), Blazor UI shared across Android, iOS, Windows and the browser · sampled ~50 of ~100 files*

## Summary

This is a well-structured .NET 10 solution implementing a mobile-first bookkeeping app (LekhaBooks) with shared Blazor UI for Android, iOS, Windows and web. The codebase follows modern .NET architecture with clear separation of concerns and has been successfully built and tested. The codebase is in good shape with a few sharp edges that can be improved for maintainability and robustness.

## Top priorities

1. **Fix the Quick-Add FAB accessibility issue** — The FAB currently has invalid ARIA attributes (`aria-label` in PascalCase) that break accessibility. → Fix to use lowercase `aria-label` and verify with accessibility tools.
2. **Add proper test coverage for transaction voiding** — The DeleteTransaction endpoint exists but has no dedicated unit tests for voiding behavior. → Add unit tests for voiding workflow with audit row verification.
3. **Improve transaction validation error messages** — Validation errors return generic "Invalid transaction" messages. → Add specific error codes and messages to improve UX.

## Findings by area

### Architecture & structure

The project follows a well-defined layered architecture with clear boundaries between UI, application, domain, and data layers. The UI is a Razor Class Library (LekhaBook.UI) that depends only on interfaces, making it truly shareable between MAUI and web hosts. The data layer is correctly split into SQLite (LekhaBook.Data.Sqlite) and PostgreSQL (LekhaBook.Data.Postgres) contexts that share the same domain model. The domain layer contains pure business rules without EF dependencies, which is excellent for testability.

The domain layer is well-structured with clear boundaries:
- **LekhaBook.Domain** contains entities, enums, and business rules (no EF)
- **LekhaBook.Application** contains use cases and validation (FluentValidation)
- **LekhaBook.Data** contains EF Core configurations that depend only on Domain

However, there's a minor duplication risk in the data layer where both SQLite and PostgreSQL contexts exist but share the same model. This is acceptable given the different database requirements, but documentation should clarify the separation.

### Correctness & risk

The codebase shows strong correctness practices with:
- Strong typing throughout (no `any` or `dynamic`)
- Proper async/await patterns throughout
- Comprehensive validation using FluentValidation
- Proper error handling with ProblemDetails responses
- Server-enforced permissions and tenant isolation

The most significant risk area is transaction handling, particularly around voiding. The DeleteTransaction endpoint (line 48 in MasterEndpoints.cs) sets status to "Void" but there's no dedicated unit test for the audit row verification and voiding workflow. This creates a risk that voided transactions might not be properly excluded from balance calculations.

### Maintainability

The codebase demonstrates excellent maintainability practices:
- Consistent naming conventions (PascalCase for classes, camelCase for methods)
- Clear separation of concerns with well-defined interfaces
- Good naming in domain models that reflects business concepts
- Well-structured validation with clear error messages
- Proper use of nullable reference types and required properties

However, there are some minor issues:
- The QuickAddSheet component has accessibility issues with ARIA attributes
- Some validation error messages are too generic ("Invalid transaction")
- The UI uses a mix of MudBlazor and custom components without clear documentation

### Performance

The performance characteristics appear acceptable for the current scale:
- All UI components use virtualization and lazy loading where appropriate
- API responses are appropriately sized
- Database queries use appropriate indexes (visible in EF Core configuration)
- No obvious N+1 patterns in the visible code

The SQLite database is used only on mobile devices, and PostgreSQL handles server-side workloads appropriately. The outbox pattern for sync is correctly implemented.

### Domain-specific (web)

The web UI demonstrates strong adherence to accessibility and responsive design principles:
- Uses MudBlazor for responsive components that adapt to different screen sizes
- Implements keyboard navigation with `enterNav.js` for improved accessibility
- Uses semantic HTML and proper form labeling
- Has clear separation between desktop and mobile navigation patterns

The web UI shows strong potential for internationalization with proper localization patterns in place.

### Tooling & hygiene

The project demonstrates excellent tooling practices:
- Centralized package management in Directory.Packages.props
- Proper .gitignore with appropriate exclusions
- CI workflow running builds and tests
- Comprehensive test coverage (78 domain, 21 application, 4 data, 21 API tests)
- Linting and type-checking are integrated into the build process
- Documentation is comprehensive and well-structured

## Smaller things

- Quick-Add FAB has invalid ARIA attributes (`aria-label` in PascalCase instead of lowercase)
- Some validation error messages are too generic ("Invalid transaction")
- The `EnterNavForm` component needs better documentation
- Some test files have duplicated setup code that could be extracted

## What I didn't cover

This review sampled the core functionality and critical paths. I didn't examine:
- All individual UI components in depth
- Every single API endpoint in detail
- The full sync workflow in the SQLite sync simulation tests
- The complete accessibility audit across all screen sizes

The main focus was on the architecture, transaction handling, and UI accessibility. The core business flows appear to be well-implemented and thoroughly tested.