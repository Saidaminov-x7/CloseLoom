# Architecture

Core defines process identity, a termination decision policy, and interfaces for process, window, allowlist and activity access. Infrastructure provides Windows adapters and JSON persistence. Desktop is a WPF shell using dependency injection. Tests exercise policy and storage without terminating real processes.

A grace-period coordinator is implemented and unit-tested with virtual time; the event-driven monitoring and end-to-end connections are incomplete. In particular, a policy decision is not a substitute for tracking every state change throughout a grace period. The UI must not expose active termination until the complete pipeline and protection rules have been reviewed and tested.
