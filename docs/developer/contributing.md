# Contributing Guide

## How to Contribute to WeredaConnect

Thank you for your interest in contributing! This guide will help you get started.

## Audience
- External contributors
- New team members
- Open source developers

---

## Getting Started

### 1. Read Documentation

Familiarize yourself with the project:
- **[Getting Started](./getting-started.md)** - Environment setup
- **[System Architecture](../architecture/system-architecture.md)** - Architecture overview
- **[Development Workflow](./dev-workflow.md)** - Development process
- **[Local Reality](../local_reality/)** - Ethiopian context

### 2. Set Up Development Environment

Follow the [Getting Started Guide](./getting-started.md) to set up your local environment.

### 3. Find an Issue

- Browse [GitHub Issues](https://github.com/your-org/WeredaConnect/issues)
- Look for `good-first-issue` or `help-wanted` labels
- Comment on the issue to claim it

### 4. Join the Community

- **Slack/Discord**: [Join here](#) (Coming Soon)
- **Email**: dev@weredaconnect.com
- **Weekly Meetings**: Thursdays 3pm EAT (Coming Soon)

---

## Contribution Process

### 1. Fork and Clone

```bash
# Fork the repository on GitHub
# Then clone your fork
git clone https://github.com/YOUR-USERNAME/WeredaConnect.git
cd WeredaConnect

# Add upstream remote
git remote add upstream https://github.com/your-org/WeredaConnect.git
```

### 2. Create a Branch

```bash
# Update main
git checkout main
git pull upstream main

# Create feature branch
git checkout -b feature/your-feature-name
```

### 3. Make Changes

- Write code following [coding standards](.ai/standards.md)
- Write tests (see [Testing Guidelines](./testing-guidelines.md))
- Update documentation
- Follow offline-first principles
- Consider Ethiopian context (see [Local Reality](../local_reality/))

### 4. Test Your Changes

```bash
# Mobile
cd mobile
flutter test
flutter analyze

# Backend
cd server
dotnet test
dotnet format --verify-no-changes

# Infrastructure
cd infra
npm test
npm run lint
```

### 5. Commit Changes

```bash
# Stage changes
git add .

# Commit with conventional commit message
git commit -m "feat: add offline sync for reports"

# Pre-commit hooks will run automatically
```

**Commit Message Format**:
```
<type>(<scope>): <subject>

<body>

<footer>
```

**Types**: `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`

### 6. Push and Create PR

```bash
# Push to your fork
git push origin feature/your-feature-name

# Create Pull Request on GitHub
# - Fill in PR template
# - Link related issues
# - Request reviewers
```

---

## Code Standards

### Mobile (Flutter/Dart)

```dart
// ✅ Good: Offline-first pattern
Future<Report> getReport(String id) async {
  // Try local first
  final local = await _localDataSource.getReport(id);
  if (local != null) return local;
  
  // Check connectivity
  if (!await _connectivity.isOnline) {
    throw OfflineException();
  }
  
  // Fetch from remote
  final remote = await _remoteDataSource.getReport(id);
  await _localDataSource.saveReport(remote);
  return remote;
}

// ❌ Bad: Online-first (doesn't work offline)
Future<Report> getReport(String id) async {
  return await _api.getReport(id);
}
```

### Backend (.NET)

```csharp
// ✅ Good: AOT-compatible, uses NSubstitute
public class ReportService
{
    private readonly IReportRepository _repository;
    
    public ReportService(IReportRepository repository)
    {
        _repository = repository;
    }
}

// ❌ Bad: Uses Moq (not AOT-compatible)
var mock = new Mock<IReportRepository>();
```

### Infrastructure (CDK)

```typescript
// ✅ Good: Cost-optimized
const lambda = new lambda.Function(this, 'CreateReport', {
  runtime: lambda.Runtime.PROVIDED_AL2023,
  architecture: lambda.Architecture.ARM_64,  // 20% cheaper
  memorySize: 512,
  timeout: Duration.seconds(10),
  reservedConcurrentExecutions: 5,  // Prevent runaway costs
});

// ❌ Bad: Expensive, no limits
const lambda = new lambda.Function(this, 'CreateReport', {
  runtime: lambda.Runtime.PROVIDED_AL2023,
  architecture: lambda.Architecture.X86_64,
  memorySize: 3008,  // Overkill
  timeout: Duration.seconds(900),  // Too long
});
```

---

## Offline-First Requirements

### All Features Must Work Offline

**Required**:
- ✅ Save data locally first
- ✅ Queue operations for sync
- ✅ Show sync status to user
- ✅ Handle conflicts gracefully
- ✅ Test offline scenarios FIRST

**Example Checklist**:
- [ ] Data saved to local database (Isar)
- [ ] Operation added to sync queue
- [ ] UI shows "Pending Sync" indicator
- [ ] Syncs automatically when online
- [ ] Handles conflicts (server wins, client merges)
- [ ] Tests cover offline scenarios

---

## Ethiopian Context Validation

### Consider Local Constraints

**Network**:
- ✅ Works on 3G (1-5 Mbps)
- ✅ Handles intermittent connectivity
- ✅ Minimizes data usage

**Devices**:
- ✅ Runs on 2GB RAM devices
- ✅ Works on Android 8+
- ✅ Optimized battery usage

**Localization**:
- ✅ Supports Amharic, Afan Oromo, English
- ✅ Uses appropriate date/time formats
- ✅ Culturally appropriate UI/UX

**Example**:
```dart
// ✅ Good: Compressed photos for data savings
final compressed = await FlutterImageCompress.compressWithQuality(
  image,
  quality: 80,  // Good balance
);

// ❌ Bad: Full resolution (wastes data)
await uploadPhoto(originalImage);
```

---

## AOT Compliance (Backend)

### Required for .NET Code

**✅ Allowed**:
- NSubstitute (mocking)
- System.Text.Json (serialization)
- Source generators
- Explicit DI registration

**❌ Banned**:
- Moq (uses reflection)
- Newtonsoft.Json (not AOT-compatible)
- Entity Framework Core (not AOT-compatible)
- Dynamic code generation

**Verification**:
```bash
# Test AOT compilation
dotnet publish -c Release -r linux-arm64 /p:PublishAot=true

# Should succeed without warnings
```

---

## Pull Request Guidelines

### PR Template

```markdown
## Description
Brief description of changes

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation update

## Offline-First Compliance
- [ ] Works offline
- [ ] Syncs when online
- [ ] Handles conflicts
- [ ] Offline tests added

## Ethiopian Context
- [ ] Works on 3G
- [ ] Optimized for low-end devices
- [ ] Localization considered
- [ ] Data usage minimized

## AOT Compliance (Backend only)
- [ ] No reflection usage
- [ ] No dynamic code generation
- [ ] AOT compilation tested
- [ ] Uses approved libraries

## Testing
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Manual testing completed
- [ ] Coverage maintained/improved

## Checklist
- [ ] Code follows style guidelines
- [ ] Self-review completed
- [ ] Documentation updated
- [ ] No new warnings
- [ ] Tests pass locally
```

### Review Process

1. **Automated Checks** (must pass):
   - Tests
   - Linting
   - Formatting
   - AOT compilation (backend)

2. **Code Review** (1-2 reviewers):
   - Code quality
   - Offline-first compliance
   - Ethiopian context considerations
   - Test coverage

3. **Approval**: Requires 1 approval from maintainer

4. **Merge**: Squash and merge (default)

---

## Testing Requirements

### Minimum Coverage

- **Mobile**: >80% for core logic
- **Backend**: >75% for business logic
- **Infrastructure**: >70% for custom constructs

### Test Offline Scenarios First

```dart
group('Offline scenarios', () {
  setUp(() async {
    await setConnectivity(false);
  });

  test('should save report locally when offline', () async {
    // Test implementation
  });
});

group('Online scenarios', () {
  setUp(() async {
    await setConnectivity(true);
  });

  test('should sync report when online', () async {
    // Test implementation
  });
});
```

---

## Documentation Requirements

### Update Documentation

When making changes, update:
- **Code comments**: For complex logic
- **README.md**: If setup changes
- **API docs**: If API changes
- **Architecture docs**: If architecture changes
- **User docs**: If user-facing changes

### Documentation Style

- Clear and concise
- Include code examples
- Consider non-technical readers
- Use diagrams where helpful

---

## Mentoring New Contributors

### Be Patient
- Offline-first is a paradigm shift
- Ethiopian context may be unfamiliar
- AOT compliance has learning curve

### Provide Examples
- Point to existing code patterns
- Share relevant documentation
- Offer to pair program

### Encourage Questions
- Better to ask than assume
- No question is too basic
- We're all learning

---

## Recognition

Contributors will be:
- Listed in CONTRIBUTORS.md (if file exists)
- Credited in release notes
- Appreciated by the community
- Invited to contributor meetings

---

## License

By contributing, you agree that your contributions will be licensed under the same license as the project (see LICENSE file).

---

## Questions?

If you have questions about contributing:

1. Check the documentation
2. Search existing issues
3. Open a new issue with the `question` label
4. Email: dev@weredaconnect.com
5. Join community chat (Coming Soon)

---

## Related Documentation

- **[Getting Started](./getting-started.md)** - Environment setup
- **[Development Workflow](./dev-workflow.md)** - Development process
- **[Testing Guidelines](./testing-guidelines.md)** - Testing practices
- **[Coding Standards](.ai/standards.md)** - Code style guide

---

**Thank you for contributing to WeredaConnect!** 🙏

Together, we're building a platform that empowers Ethiopian communities to engage with their local government and improve their neighborhoods.

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Maintainer**: Community Team

