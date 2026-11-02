# Security and limitations

Active termination is disabled: WindowsProcessTerminator refuses every request. The monitor enforces Dry Run even if a settings file contains a different value. Explicit per-application background consent is checked by the policy. Process and window observations that cannot be verified must cancel or suspend pending work; the implementation requires independent validation against real Windows behavior.

PID reuse, executable path and current session are checked, and visible top-level windows including owned dialogs block a decision. Windows does not establish that an application has finished saving. An app may continue useful work in the tray after its visible windows close. Forced termination can destroy unsaved data.

There is no reliable way in this version to identify every third-party security product or prove continuous event delivery. A name-based list cannot establish safety. Do not enable real termination until a reviewed fail-closed protection design and authorized integration tests address these gaps. Never infer permission from an executable name alone or use tree termination.

Logs must not contain secrets, window contents or private documents. Application names, identifiers and paths can themselves be sensitive; review data before sharing.
