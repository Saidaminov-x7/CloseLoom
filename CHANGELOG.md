# Changelog

## Unreleased

- Added Windows event and process monitoring integration, lifecycle controls, and fail-closed handling for uncertain observation.
- Expanded isolated tests and protected the activity log export and size limit.
- Connected application settings, activity, logging and tray controls to services.
- Enforced Dry Run and retained the disabled native termination adapter.
- Completed live Windows 11 monitoring tests with disposable windows: 38/38 PASS in Dry Run.
- Fixed invalidated HWND DESTROY handling, confirmed exits and WPF reentrant-close error.
- Prevented repeated Dry Run decisions until a main window is observed again.
- Added two Core regression tests, bringing isolated unit tests to 63/63 PASS.
- Included reproducible live-Windows test source code and instructions.
- Active termination and tray menu interactions remain NOT TESTED.
