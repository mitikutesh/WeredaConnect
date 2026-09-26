/// Short browser description ("Chrome on Windows") from a user agent.
///
/// Keeps only the browser family and OS family, never the full UA string.
/// Pure Dart so it can be unit-tested on the VM.
String describeUserAgent(String ua) {
  final browser = ua.contains('Edg/')
      ? 'Edge'
      : ua.contains('OPR/')
          ? 'Opera'
          : ua.contains('Firefox/')
              ? 'Firefox'
              : ua.contains('Chrome/')
                  ? 'Chrome'
                  : ua.contains('Safari/')
                      ? 'Safari'
                      : 'Browser';
  final os = ua.contains('Android')
      ? 'Android'
      : (ua.contains('iPhone') || ua.contains('iPad'))
          ? 'iOS'
          : ua.contains('Windows')
              ? 'Windows'
              : ua.contains('Mac OS')
                  ? 'macOS'
                  : ua.contains('Linux')
                      ? 'Linux'
                      : null;
  return os == null ? browser : '$browser on $os';
}
