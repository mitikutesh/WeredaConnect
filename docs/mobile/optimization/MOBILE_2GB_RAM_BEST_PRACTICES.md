# 📱 Flutter 2GB RAM Best Practices Guide

## Target: Low-end Android devices (2GB RAM, Android 8+, 3G networks)

---

## 1. Widget Optimization

### ✅ DO: Use Const Constructors
```dart
// Good - prevents rebuilds
class MyWidget extends StatelessWidget {
  const MyWidget({super.key});
  
  @override
  Widget build(BuildContext context) {
    return const Text('Hello');
  }
}
```

### ❌ DON'T: Create Widgets in Build
```dart
// Bad - creates new widget every frame
@override
Widget build(BuildContext context) {
  return MyExpensiveWidget(); // New instance every frame!
}
```

### ✅ DO: Limit Widget Tree Depth
```dart
// Good - shallow tree
Column(
  children: [
    Text('Title'),
    Text('Subtitle'),
  ],
)

// Bad - deep nesting
Column(
  children: [
    Container(
      child: Padding(
        child: Center(
          child: Text('Title'),
        ),
      ),
    ),
  ],
)
```

---

## 2. Image Optimization

### ✅ DO: Use cached_network_image
```dart
CachedNetworkImage(
  imageUrl: 'https://example.com/image.jpg',
  placeholder: (context, url) => Shimmer.fromColors(...),
  errorWidget: (context, url, error) => Icon(Icons.error),
  cacheManager: CacheManager.instance,
)
```

### ✅ DO: Compress Images Before Upload
```dart
// Compress to <500KB before sending
final compressed = await _compressImage(imageFile);
await _uploadToServer(compressed);
```

### ❌ DON'T: Load Full-Resolution Images
```dart
// Bad - loads 4MB image for 100x100 display
Image.network(
  'https://example.com/4mb-image.jpg',
  width: 100,
  height: 100,
)
```

---

## 3. State Management (BLoC)

### ✅ DO: Dispose Streams Properly
```dart
class MyBloc extends Bloc<MyEvent, MyState> {
  late final StreamSubscription _subscription;
  
  MyBloc() : super(InitialState()) {
    _subscription = _someStream.listen((event) {
      add(MyEvent());
    });
  }
  
  @override
  Future<void> close() {
    _subscription.cancel();
    return super.close();
  }
}
```

### ✅ DO: Use BlocListener for Side Effects
```dart
BlocListener<MyBloc, MyState>(
  listener: (context, state) {
    if (state is ErrorState) {
      ScaffoldMessenger.of(context).showSnackBar(...);
    }
  },
  child: BlocBuilder<MyBloc, MyState>(
    builder: (context, state) {
      return MyWidget();
    },
  ),
)
```

---

## 4. Database (Isar) Optimization

### ✅ DO: Limit Query Results
```dart
// Good - fetch only needed data
final recent = await isar.reports
  .where()
  .limit(50) // Limit to 50 items
  .findAll();
```

### ❌ DON'T: Load All Data
```dart
// Bad - loads entire database into memory
final allReports = await isar.reports.where().findAll();
```

### ✅ DO: Use Indexes for Frequent Queries
```dart
@collection
class Report {
  Id id = Isar.autoIncrement;
  
  @Index() // Add index for faster queries
  DateTime createdAt = DateTime.now();
}
```

---

## 5. Network Optimization

### ✅ DO: Implement Request Batching
```dart
// Batch multiple requests into one
final response = await _apiClient.post('/batch', data: {
  'operations': [
    {'type': 'create', 'data': report1},
    {'type': 'update', 'data': report2},
  ],
});
```

### ✅ DO: Use Gzip Compression
```dart
// Dio automatically compresses requests
final dio = Dio();
dio.options.headers['Accept-Encoding'] = 'gzip';
```

### ❌ DON'T: Make Unnecessary Requests
```dart
// Bad - requests same data repeatedly
for (int i = 0; i < 100; i++) {
  await _apiClient.get('/user'); // 100 requests!
}
```

---

## 6. Memory Leak Prevention

### ✅ DO: Cancel Subscriptions
```dart
class MyWidget extends StatefulWidget {
  @override
  State<MyWidget> createState() => _MyWidgetState();
}

class _MyWidgetState extends State<MyWidget> {
  late StreamSubscription _subscription;
  
  @override
  void initState() {
    super.initState();
    _subscription = _stream.listen((_) {});
  }
  
  @override
  void dispose() {
    _subscription.cancel();
    super.dispose();
  }
}
```

### ✅ DO: Use WeakReferences for Callbacks
```dart
// Prevent circular references
class MyService {
  WeakReference<MyCallback>? _callback;
  
  void setCallback(MyCallback callback) {
    _callback = WeakReference(callback);
  }
}
```

---

## 7. Performance Monitoring

### ✅ DO: Profile on Real Device
```bash
# Connect 2GB RAM device
flutter run --profile

# Open DevTools
devtools

# Monitor:
# - Memory usage
# - Frame rate
# - CPU usage
```

### ✅ DO: Use Performance Overlay
```dart
MaterialApp(
  showPerformanceOverlay: kDebugMode,
  // ...
)
```

---

## 8. APK Size Optimization

### ✅ DO: Build with Split APKs
```bash
flutter build apk --release --split-per-abi
# Creates separate APKs for ARM32/ARM64
# Reduces download size by 30-40%
```

### ✅ DO: Enable Code Shrinking
```gradle
// android/app/build.gradle
buildTypes {
  release {
    minifyEnabled true
    shrinkResources true
    proguardFiles getDefaultProguardFile('proguard-android-optimize.txt')
  }
}
```

---

## 9. Offline-First Patterns

### ✅ DO: Cache Responses Locally
```dart
// Store in Isar, serve from cache
final cached = await _isar.reports.where().findAll();
if (cached.isNotEmpty) {
  return cached; // Serve from cache
}

// Sync in background
_syncQueue.enqueue(SyncOperation(...));
```

### ✅ DO: Implement Conflict Resolution
```dart
// Server version wins
if (serverVersion > localVersion) {
  await _updateLocal(serverData);
}
```

---

## 10. Testing on Low-End Devices

### ✅ DO: Test on 2GB Emulator
```bash
# Create low-end emulator
flutter emulators create --name low_end

# Run tests
flutter test --device-id low_end
```

### ✅ DO: Monitor Memory During Tests
```bash
# Use Android Profiler
adb shell dumpsys meminfo et.wereda.connect
```

---

## Performance Targets

| Metric | Target | How to Measure |
|--------|--------|----------------|
| Initial RAM | <50MB | DevTools Memory tab |
| Peak RAM | <100MB | DevTools Memory tab |
| APK Size | <50MB | `ls -lh build/app/outputs/` |
| Load Time | <3s | `flutter run` startup time |
| Frame Rate | 60 FPS | Performance Overlay |
| Battery | <5%/hour | Battery Historian |

---

## Checklist for Feature Development

- [ ] Use const constructors
- [ ] Limit widget tree depth
- [ ] Compress images before upload
- [ ] Dispose streams/subscriptions
- [ ] Limit database queries
- [ ] Batch network requests
- [ ] Profile on 2GB device
- [ ] Test offline mode
- [ ] Monitor APK size
- [ ] No memory leaks detected

---

**Last Updated**: 2026-02-10
**Target Devices**: Android 8+ (API 26), 2GB RAM
**Context**: Ethiopian 3G networks, offline-first agritech

