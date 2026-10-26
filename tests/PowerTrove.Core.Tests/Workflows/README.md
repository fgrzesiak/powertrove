# Workflow planning regression checks

Later workflow steps must implement `IWorkflowStepPlanner`. Planning checks
availability and parameters using projected context metadata, without opening
intermediate files. Unsupported planning blocks the preview and workflow
validation. Existing actions without this contract cannot be used as executable
later steps until they provide a safe planner.

`WorkflowRunnerTests` covers incompatible content kinds, capabilities that must
not transfer to generated files, unavailable steps, invalid parameters, missing
planners, propagation through three steps, and cancellation. Strict mocks ensure
later steps do not call normal matching, preview, or execution during planning.

Windows validation broker checks remaining:

- Run the workflow regression tests and architecture localization checks.
- Verify that a recipe containing an unsupported later step cannot be started
  from the palette and displays the planning warning in English and German.
- Build the desktop app and check that ordinary single-step actions still work.

The PDF composition findings remain outside this repair's permitted edit paths
(`tools/` is excluded); these tests do not claim PDF validation.
