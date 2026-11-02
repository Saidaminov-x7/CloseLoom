# Testing

Use the restore, Release build, test and format commands in the README. Unit tests use fake process catalogs, window sources and clocks, or temporary JSON files. They do not start AppGuard, install WinEvent hooks, observe real processes or terminate them. A passing unit test is not evidence of actual Windows behavior.

UI, tray interactions, live window events, actual process observation and termination remain NOT TESTED. GitHub Actions runs these safe checks on Windows; a local run of equivalent commands does not prove a remote workflow has executed.
