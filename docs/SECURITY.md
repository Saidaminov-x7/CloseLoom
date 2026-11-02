# Security and limitations

This project is incomplete. Monitoring is not connected and active termination must remain disabled. Dry Run defaults to enabled. The native termination adapter refuses termination.

Identity must be verified using PID, creation time, executable path and current session; access errors must fail closed. Window enumeration failures are not evidence that no windows exist. A visible owned dialog or minimized window must prevent termination. Windows does not prove that application data is saved when a window disappears. Background work and tray applications require explicit consent, and a reopened window or changed process state must cancel any pending action.

The project does not yet have a reliable way to identify every third-party security product or prove that an application has completed background work. A name-based list alone cannot establish safety. No process should be terminated until these gaps are addressed with a reviewed design. Never infer permission from an executable name alone, use tree termination, or bypass Windows protections.

Logs must not contain secrets, window content or private documents. Local paths and application names can themselves be sensitive; review logs before sharing them.
