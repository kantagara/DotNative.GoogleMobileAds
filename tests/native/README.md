# Native validation harness

Android harness compiles the production plugin sources against the real OS/Google SDK. Its channel and NativeHost stubs only expose the entry points for instrumentation; it does not replace native rendering or Google consent gates. It is not a test of the complete DotNative engine's view registry/transport.

Requires JDK 17, Android SDK 36 and an emulator (tested API 36.1).

```sh
./tests/native/android/gradlew -p tests/native/android :app:connectedDebugAndroidTest
```

The runner selects the test class explicitly to avoid legacy runner discovery scanning Google SDK classes. Tests use a local generated PDF or reject ad loads before consent. The manifest uses Google's demo app ID in the ads harness. No ad requests, clicks, impressions or real accounts are used.

2026-10-13 combined Android API 36.1 harness: PDF navigation/cancel/reopen/invalid page plus Ads consent gate passed (2/2). No live ad fill was requested. The split per-plugin harnesses compile the same production sources.
