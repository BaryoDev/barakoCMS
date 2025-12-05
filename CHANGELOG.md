# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2025-12-05

### Added
- **Runtime Validation**: Implemented comprehensive validation for Content Types and Content Data.
  - Enforces field types (`string`, `int`, `bool`, `datetime`, `decimal`, `array`, `object`).
  - Enforces PascalCase naming convention for fields.
  - Validates content data against schema on Create and Update.
- **Validation Configuration**: Added `StrictValidation` and `ValidationOptions` to `appsettings.json`.
- **Documentation**: Added `RELEASE_PROCESS.md` and updated `DEVELOPMENT_STANDARDS.md` with validation details.

### Fixed
- **Integration Tests**: Resolved Marten async query issues in validators.
- **JSON Handling**: Fixed `ContentDataValidator` to correctly handle `JsonElement` types.

## [1.0.3] - 2024-01-01

### Added
- **AI Adoption**: Added `llms.txt` and `.cursorrules` to improve AI agent compatibility.
- **Community**: Added `CONTRIBUTING.md` and `CODE_OF_CONDUCT.md`.
- **Production**: Added `Dockerfile` and updated `docker-compose.yml` with health checks.
- **Health Checks**: Added `/health` endpoint.
- **Documentation**: Added `CITATIONS.cff` for research citation.

### Changed
- **Licensing**: Changed license from custom restrictive license to **Apache License 2.0**.
- **NuGet**: Updated package tags to include `ai-native` and `vibe-coding`.
- **Error Handling**: Enabled global exception handling with `UseProblemDetails()`.

### Fixed
- Improved `docker-compose.yml` reliability with `depends_on` and health checks.
