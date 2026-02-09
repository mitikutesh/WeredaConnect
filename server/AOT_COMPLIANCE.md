# .NET 10 Native AOT Compliance

## ⚠️ CRITICAL DOCUMENT
**Read this BEFORE adding any NuGet package or writing reflection-based code.**

Native AOT (Ahead-of-Time) compilation provides fast cold starts and low memory usage, but it has strict limitations. Violating these rules will cause **build failures** after hours of work.

---

## ❌ BANNED LIBRARIES (Use Reflection/JIT)

### JSON Serialization
- **Newtonsoft.Json** ❌
  - **Why**: Uses reflection for serialization
  - **Use instead**: `System.Text.Json` with source generators

### ORM / Database
- **Entity Framework Core** ❌
  - **Why**: Heavy reflection, not AOT-compatible
  - **Use instead**: Dapper, AWS SDK for DynamoDB, or raw ADO.NET

### Dependency Injection
- **Autofac** ❌ (some features)
  - **Why**: Dynamic assembly scanning
  - **Use instead**: Built-in `Microsoft.Extensions.DependencyInjection` with explicit registration

### Mocking (Testing)
- **Moq** ❌
  - **Why**: Uses `Castle.DynamicProxy` (reflection)
  - **Use instead**: NSubstitute or manual mocks

### Mapping
- **AutoMapper** ❌
  - **Why**: Reflection-based mapping
  - **Use instead**: Manual mapping or source-generated mappers

### Validation
- **FluentValidation** ⚠️ (limited support)
  - **Why**: Some features use reflection
  - **Use instead**: Manual validation or DataAnnotations with source generators

---

## ✅ REQUIRED PATTERNS

### 1. JSON Serialization with Source Generators

#### ❌ BAD (Reflection-based)
```csharp
var json = JsonSerializer.Serialize(user); // Uses reflection
```

#### ✅ GOOD (Source Generator)
```csharp
// Define a JsonSerializerContext
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(List<UserDto>))]
[JsonSerializable(typeof(CreateReportDto))]
public partial class AppJsonContext : JsonSerializerContext { }

// Use it explicitly
var json = JsonSerializer.Serialize(user, AppJsonContext.Default.UserDto);
var user = JsonSerializer.Deserialize<UserDto>(json, AppJsonContext.Default.UserDto);
```

**Why**: Source generators create serialization code at compile time, no reflection needed.

---

### 2. Dependency Injection - Explicit Registration

#### ❌ BAD (Assembly Scanning)
```csharp
services.Scan(scan => scan
    .FromAssemblyOf<Startup>()
    .AddClasses()
    .AsImplementedInterfaces());
```

#### ✅ GOOD (Explicit)
```csharp
services.AddSingleton<IUserService, UserService>();
services.AddScoped<IReportRepository, ReportRepository>();
services.AddTransient<ISyncService, SyncService>();

// Validate at build time
services.ValidateOnBuild();
```

**Why**: AOT needs to know all types at compile time.

---

### 3. Avoid Reflection APIs

#### ❌ BAD
```csharp
var type = Type.GetType("MyNamespace.MyClass");
var instance = Activator.CreateInstance(type);
var method = type.GetMethod("MyMethod");
method.Invoke(instance, null);
```

#### ✅ GOOD
```csharp
var instance = new MyClass();
instance.MyMethod();
```

**Why**: Reflection is not available in AOT.

---

### 4. LINQ - Use Simple Expressions

#### ❌ BAD (Dynamic LINQ)
```csharp
var query = context.Users.Where("Age > 18"); // String-based
```

#### ✅ GOOD (Strongly-typed)
```csharp
var query = context.Users.Where(u => u.Age > 18);
```

**Why**: Dynamic LINQ uses reflection.

---

### 5. Configuration - Bind Explicitly

#### ❌ BAD (Reflection-based binding)
```csharp
var config = Configuration.Get<MyConfig>(); // Uses reflection
```

#### ✅ GOOD (Explicit binding)
```csharp
var config = new MyConfig
{
    ApiKey = Configuration["ApiKey"],
    Timeout = int.Parse(Configuration["Timeout"])
};
```

**Why**: Reflection-based binding not supported in AOT.

---

## ✅ APPROVED LIBRARIES

### AWS SDK
- **AWSSDK.DynamoDBv2** ✅
- **AWSSDK.S3** ✅
- **AWSSDK.Lambda** ✅
- **Amazon.Lambda.Core** ✅
- **Amazon.Lambda.APIGatewayEvents** ✅
- **Amazon.Lambda.Serialization.SystemTextJson** ✅

### Data Access
- **Dapper** ✅ (lightweight, minimal reflection)
- **Npgsql** ✅ (PostgreSQL, if needed)

### Testing
- **xUnit** ✅
- **NSubstitute** ✅
- **FluentAssertions** ✅

### Utilities
- **System.Text.Json** ✅ (with source generators)
- **Microsoft.Extensions.DependencyInjection** ✅
- **Microsoft.Extensions.Logging** ✅

---

## 🔍 AOT Validation Checklist

Before committing code, verify:

- [ ] No banned libraries in `.csproj`
- [ ] All JSON serialization uses `AppJsonContext`
- [ ] No `Type.GetType()`, `Activator.CreateInstance()`, etc.
- [ ] All DI registrations are explicit
- [ ] Build succeeds with `dotnet publish -c Release /p:PublishAot=true`
- [ ] No runtime errors related to missing types

---

## 🛠️ Testing AOT Compatibility

### Local Build
```bash
dotnet publish -c Release /p:PublishAot=true
```

If this fails, you have AOT-incompatible code.

### CI/CD
Add this to your GitHub Actions workflow:
```yaml
- name: Validate AOT Compatibility
  run: dotnet publish -c Release /p:PublishAot=true
```

---

## 📚 Resources

### Official Docs
- [.NET Native AOT Deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [System.Text.Json Source Generators](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation)

### AWS Lambda
- [.NET on AWS Lambda](https://docs.aws.amazon.com/lambda/latest/dg/lambda-csharp.html)
- [Native AOT on Lambda](https://aws.amazon.com/blogs/compute/introducing-the-net-8-runtime-for-aws-lambda/)

---

## 🚨 Common Errors

### Error: "Type 'X' is not compatible with AOT"
- **Cause**: Using reflection or dynamic code
- **Fix**: Refactor to use explicit types and source generators

### Error: "JsonSerializer requires source generator"
- **Cause**: Missing `[JsonSerializable]` attribute
- **Fix**: Add type to `AppJsonContext`

### Error: "Cannot resolve service 'X'"
- **Cause**: DI registration missing or uses assembly scanning
- **Fix**: Explicitly register service in `ConfigureServices`

---

## 📝 Update Policy

This document must be updated when:
- Adding a new NuGet package (verify AOT compatibility)
- Discovering a new AOT limitation
- .NET version upgrade (check for new AOT features)

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Next Review**: After .NET 10 GA release

